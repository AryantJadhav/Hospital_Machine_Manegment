using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Wiring that only applies when the Service Control Manager started us.
///
/// Lives in its own class because the platform analyser does not honour
/// [SupportedOSPlatform] on local functions inside top-level statements, and
/// suppressing the warning would hide the same mistake made for real later.
/// </summary>
public static class WindowsServiceSetup
{
    /// <summary>
    /// Sends logs to the Windows Event Log.
    ///
    /// This is where a hospital's IT contact — or whoever they call — will
    /// actually look, because a service that failed to start has no console
    /// to have printed to.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void AddEventLog(WebApplicationBuilder builder) =>
        builder.Logging.AddEventLog(NameSource);

    [SupportedOSPlatform("windows")]
    private static void NameSource(EventLogSettings settings) =>
        settings.SourceName = "Hospital PM";
}
