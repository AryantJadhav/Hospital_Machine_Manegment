using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

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
