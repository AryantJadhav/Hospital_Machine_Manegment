namespace HospitalPm.Api.Hosting;

/// <summary>
/// Source-generated startup messages. A service has no console, so anything
/// worth knowing at start-up has to reach the log properly.
/// </summary>
public static partial class StartupLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Using {Directory} for data because the system location is not writable. " +
                  "On an installed service this is wrong: the signing key belongs somewhere " +
                  "only the service account can read. Set {Variable} to a protected directory.")]
    public static partial void DataDirectoryFallback(ILogger logger, string directory, string variable);
}
