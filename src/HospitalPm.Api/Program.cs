using System.Reflection;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Hosting;
using HospitalPm.Api.Checklists;
using HospitalPm.Api.Equipment;
using HospitalPm.Api.Labels;
using HospitalPm.Api.Locations;
using HospitalPm.Api.Maintenance;
using HospitalPm.Api.Operations;
using HospitalPm.Api.Reports;
using HospitalPm.Api.WorkOrders;
using HospitalPm.Infrastructure.Identity;
using Hangfire;
using Hangfire.PostgreSql;
using HospitalPm.Infrastructure.Labels;
using HospitalPm.Infrastructure.Reports;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

// ContentRoot must be the binary's own directory, not the current working
// directory. A Windows Service starts with CWD = C:\Windows\System32 and a
// systemd unit uses whatever WorkingDirectory says, so relying on the default
// makes wwwroot unresolvable on exactly the deployments we ship to — while
// /health keeps returning 200 and every health check looks fine.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Run as a Windows Service when the Service Control Manager started us, and
// as an ordinary console app otherwise. UseWindowsService() detects which and
// no-ops off Windows, so one binary covers the hospital's service install,
// a developer pressing F5, and the Linux target.
//
// Without this the process never answers the SCM's control messages: it looks
// like it starts, then Windows reports it as unresponsive and a Stop leaves a
// half-dead service holding the port and the database connections.
builder.Host.UseWindowsService(o => o.ServiceName = "HospitalPM");

// The Windows Event Log is where a hospital's IT contact — or whoever they
// call — will actually look, because a service that failed to start has no
// console to have printed to.
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
    WindowsServiceSetup.AddEventLog(builder);
}

// Machine-specific settings the installer wrote, layered over the defaults
// shipped in appsettings.json. Kept outside the install directory so the
// database password is not readable by every local user and so uninstalling
// the program does not delete the hospital's configuration.
builder.Configuration.AddJsonFile(
    InstallPaths.SettingsFile(), optional: true, reloadOnChange: false);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<HospitalPmDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("HospitalPm")));

builder.Services.AddScoped<HospitalPm.Infrastructure.Import.EquipmentImportService>();
builder.Services.AddScoped<HospitalPm.Infrastructure.Import.LocationImportService>();

builder.Services.Configure<LabelOptions>(builder.Configuration.GetSection(LabelOptions.SectionName));
builder.Services.Configure<ScheduleOptions>(builder.Configuration.GetSection(ScheduleOptions.SectionName));
builder.Services.Configure<ReportOptions>(builder.Configuration.GetSection(ReportOptions.SectionName));
builder.Services.AddSingleton<HospitalClock>();
builder.Services.AddScoped<PmScheduleGenerator>();
builder.Services.Configure<HospitalPm.Infrastructure.Operations.BackupOptions>(
    builder.Configuration.GetSection(HospitalPm.Infrastructure.Operations.BackupOptions.Section));
builder.Services.AddSingleton<HospitalPm.Infrastructure.Operations.PgToolLocator>();
builder.Services.AddScoped<HospitalPm.Infrastructure.Operations.BackupService>();
builder.Services.AddScoped<HospitalPm.Infrastructure.Operations.DiagnosticsService>();
builder.Services.Configure<HospitalPm.Infrastructure.Licensing.LicenceOptions>(
    builder.Configuration.GetSection(HospitalPm.Infrastructure.Licensing.LicenceOptions.Section));
builder.Services.AddSingleton<HospitalPm.Infrastructure.Licensing.LicenceService>();

// Updating from a signed file on a USB stick. Scoped rather than singleton
// because it takes a backup, and BackupService is scoped around the
// DbContext it writes the run row with.
builder.Services.Configure<HospitalPm.Infrastructure.Updates.UpdateOptions>(
    builder.Configuration.GetSection(HospitalPm.Infrastructure.Updates.UpdateOptions.Section));
builder.Services.AddSingleton<HospitalPm.Domain.Updates.IUpdateFileSystem,
    HospitalPm.Infrastructure.Updates.UpdateFileSystem>();
builder.Services.AddSingleton<HospitalPm.Infrastructure.Updates.IUpdateLauncher,
    HospitalPm.Infrastructure.Updates.UpdateLauncher>();

// The only outbound HTTP in the product. It exists so an administrator can
// press "check for updates"; it never runs on a schedule, sends nothing
// about the hospital, and is inert unless Update:FeedUrl is set. The
// air-gapped promise is that the core loop never needs the network, not
// that the network is forbidden when somebody asks for it.
builder.Services
    .AddHttpClient<HospitalPm.Infrastructure.Updates.IUpdateDownloader,
                   HospitalPm.Infrastructure.Updates.UpdateDownloader>(http =>
    {
        // Long enough for a couple of hundred megabytes over a bad
        // hospital connection. The request is user-initiated and reports
        // progress, so a generous ceiling costs nothing.
        http.Timeout = TimeSpan.FromMinutes(
            Math.Clamp(
                builder.Configuration.GetValue("Update:DownloadTimeoutMinutes", 30), 1, 120));

        // A constant, deliberately carrying no machine name, licence id or
        // version. A request that identified the hospital would make this a
        // telemetry channel by accident.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HospitalPM");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Release hosting redirects, so this has to follow them - but a
        // short chain, and the downloader refuses a final address that is
        // not https.
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,

        // No cookies and no credentials: there is nothing to authenticate
        // to, and a public file fetch that carried either would be a way to
        // leak something.
        UseCookies = false,
        UseDefaultCredentials = false,
    });

builder.Services.AddScoped<HospitalPm.Infrastructure.Updates.UpdateService>();
builder.Services.Configure<FirstRunOptions>(builder.Configuration.GetSection(FirstRunOptions.Section));
builder.Services.AddSingleton<QrCodeService>();
builder.Services.AddScoped<LabelSheetService>();
builder.Services.AddScoped<ZplLabelService>();

// mDNS: advertise "hospitalpm.local" on the LAN so phones and browsers can
// find us without knowing the IP. A DHCP change on the server no longer
// breaks every bookmark and every mobile config on the ward. Disabled by
// setting Mdns:Enabled to false — for example if another mDNS responder is
// already running on this machine.
builder.Services.AddHostedService<MdnsAdvertiser>();

// QuestPDF refuses to render until a licence type is declared. Community is
// free for organisations under $1M USD annual revenue; past that it needs a
// paid licence, which is a commercial decision rather than a code one.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// The JWT signing key goes in the locked-down data directory, NOT beside the
// binary.
//
// An install test found it sitting in C:\Program Files\Hospital PM\data with
// BUILTIN\Users:(RX) inherited - every local user on the machine could read
// the key that signs authentication tokens, and anyone who can read it can
// mint a token for any user, including an administrator. A ward PC is a
// shared machine with many Windows logins, which is exactly the case where
// that matters.
//
// Keeping it with the settings, licence and backups also means the app writes
// nothing into Program Files at runtime, so an uninstall removes the install
// directory cleanly instead of leaving it behind.
builder.Services.AddHospitalPmAuth(
    builder.Configuration,
    Path.Combine(InstallPaths.DataDirectory(), "keys"));

// Hangfire in-process against the same PostgreSQL, per the packaging
// constraint: no Redis, no separate worker service, still two services on a
// client install. Skipped when no connection string is configured so the
// binary still starts for a UI-only smoke test.
var connectionString = builder.Configuration.GetConnectionString("HospitalPm");
var hasDatabase = !string.IsNullOrWhiteSpace(connectionString);

if (hasDatabase)
{
    builder.Services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString)));

    // One worker. PM generation is a nightly batch, not a throughput
    // problem, and a hospital PC is also running Postgres and a ward's
    // worth of browsers.
    builder.Services.AddHangfireServer(o => o.WorkerCount = 1);
}

var app = builder.Build();

// Migrations run on start, forward-only. A hospital has no DBA and no
// migration step in the install; the service brings its own schema up to
// date or refuses to serve. Skipped when no connection string is configured
// so the app still starts for a UI-only smoke test.
if (hasDatabase)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HospitalPmDbContext>()
        .Database.MigrateAsync();

    // Registered through IRecurringJobManager, not the static RecurringJob
    // helper. The static one reads a global JobStorage.Current, which is not
    // set when the host is built by WebApplicationFactory - so the static
    // call throws in tests and depends on initialisation order in production.
    //
    // Recurring by id, so restarting the service re-registers rather than
    // accumulating duplicate jobs.
    scope.ServiceProvider.GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<PmScheduleGenerator>(
            "pm-generate-due-dates",
            job => job.RunAsync(CancellationToken.None),
            // 18:45 UTC — 00:15 the next morning in India, a quarter of an hour
            // into the hospital's new day, so the day's PM tasks exist before
            // anyone could look for them and before the 02:30 backup captures
            // them. It ran at 00:15 UTC, which is 05:45 in India.
            //
            // Cron runs in UTC because the app deliberately avoids depending on
            // OS time zone data; the generator itself applies the hospital's
            // offset when deciding what "today" is, so firing at 18:45 UTC
            // correctly generates for the Indian day that has just begun.
            "45 18 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // 21:00 UTC — 02:30 the next morning in India, the middle of the night on
    // the hospital's own clock and the quietest the PC ever is. It ran at 02:30
    // UTC, which is 08:00 in India: the start of the day shift, exactly when a
    // biomedical department begins writing.
    //
    // The cron is UTC because the app deliberately carries no OS time zone data;
    // the offset lives in ScheduleOptions, so a hospital outside India that
    // changes it must move this line too.
    //
    // Two hours and a quarter after PM generation, so a dump holds the tasks
    // generated for the day that has just started. Both jobs share one Hangfire
    // worker, and the gap is far wider than either takes.
    scope.ServiceProvider.GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<HospitalPm.Infrastructure.Operations.BackupService>(
            "nightly-backup",
            job => job.RunScheduledAsync(CancellationToken.None),
            "0 21 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// The installer collected a hospital name and an administrator account, so
// a hospital finishes the installer with a working login rather than a web
// page asking them to invent one. Does nothing if a user already exists.
if (hasDatabase)
{
    await FirstRunSeed.ApplyAsync(app, InstallPaths.SettingsFile());

    // A backup row left Running means the process went away mid-dump - or
    // that a restore inherited one, which happens every single time.
    using (var backupScope = app.Services.CreateScope())
    {
        await InterruptedBackups.CloseAsync(
            backupScope.ServiceProvider.GetRequiredService<HospitalPmDbContext>(),
            app.Logger);
    }
}

// Said out loud rather than left to be discovered. On a developer's machine
// this is expected; on an installed service it means the signing key sits
// beside the binary, where Program Files grants every local user read access.
if (InstallPaths.UsingFallback)
{
    StartupLog.DataDirectoryFallback(
        app.Logger, InstallPaths.DataDirectory(), InstallPaths.EnvironmentVariable);
}

// Liveness probe. The Phase 2 diagnostics page and the Windows Service
// wrapper both need a cheap endpoint that proves the process is serving.
// Deliberately does not touch the database — that is a separate readiness
// check, so a DB outage stays distinguishable from a dead process.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
}));

// The React bundle is built into wwwroot and embedded in the published
// binary, so the UI ships with the app rather than as a second deployment.
// This is what makes "one binary" literally true.
app.UseAuthentication();
app.UseAuthorization();

app.MapSetupEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapEquipmentEndpoints();
app.MapImportEndpoints();
app.MapLookupEndpoints();
app.MapEquipmentHistoryEndpoints();
app.MapLocationEndpoints();
app.MapLabelEndpoints();
app.MapChecklistEndpoints();
app.MapPmEndpoints();
app.MapPmExecutionEndpoints();
app.MapWorkOrderEndpoints();
app.MapReportEndpoints();
app.MapBackupEndpoints();
app.MapDiagnosticsEndpoints();
app.MapRestoreEndpoints();
app.MapLicenceEndpoints();
app.MapUpdateEndpoints();
app.MapPilotMetricsEndpoints();
app.MapPmComplianceEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

// Client-side routing: any unmatched non-API path returns index.html so a
// deep link or a browser refresh does not 404.
app.MapFallbackToFile("index.html");

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in tests.
public partial class Program;
