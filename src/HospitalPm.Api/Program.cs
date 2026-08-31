using System.Reflection;
using HospitalPm.Api.Auth;
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

builder.Services.AddOpenApi();

builder.Services.AddDbContext<HospitalPmDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("HospitalPm")));

// The signing key lives beside the binary, not in the content root, so it
// survives an upgrade that replaces the executable and stays out of any
// directory the web server can serve.
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
builder.Services.AddSingleton<QrCodeService>();
builder.Services.AddScoped<LabelSheetService>();
builder.Services.AddScoped<ZplLabelService>();

// QuestPDF refuses to render until a licence type is declared. Community is
// free for organisations under $1M USD annual revenue; past that it needs a
// paid licence, which is a commercial decision rather than a code one.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

builder.Services.AddHospitalPmAuth(
    builder.Configuration,
    Path.Combine(AppContext.BaseDirectory, "data"));

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
            // 00:15 UTC. Cron runs in UTC because the app deliberately avoids
            // depending on OS time zone data; the generator itself applies the
            // hospital's offset when deciding what "today" is.
            "15 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // 02:30 UTC — 08:00 in India, after the night's PM generation and before
    // the day shift starts writing. A hospital PC is not busy at either.
    scope.ServiceProvider.GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<HospitalPm.Infrastructure.Operations.BackupService>(
            "nightly-backup",
            job => job.RunScheduledAsync(CancellationToken.None),
            "30 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
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

app.UseDefaultFiles();
app.UseStaticFiles();

// Client-side routing: any unmatched non-API path returns index.html so a
// deep link or a browser refresh does not 404.
app.MapFallbackToFile("index.html");

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in tests.
public partial class Program;
