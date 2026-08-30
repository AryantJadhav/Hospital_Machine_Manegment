using System.Reflection;
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

var app = builder.Build();

// Migrations run on start, forward-only. A hospital has no DBA and no
// migration step in the install; the service brings its own schema up to
// date or refuses to serve. Skipped when no connection string is configured
// so the app still starts for a UI-only smoke test.
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("HospitalPm")))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<HospitalPmDbContext>()
        .Database.MigrateAsync();
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
app.UseDefaultFiles();
app.UseStaticFiles();

// Client-side routing: any unmatched non-API path returns index.html so a
// deep link or a browser refresh does not 404.
app.MapFallbackToFile("index.html");

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in tests.
public partial class Program;
