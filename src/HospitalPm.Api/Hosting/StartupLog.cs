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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Created the first administrator account '{UserName}' from the installer's settings.")]
    public static partial void FirstRunAdminCreated(ILogger logger, string userName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not create the administrator account the installer asked for: {Reason}. " +
                  "Open the application in a browser to finish setting it up.")]
    public static partial void FirstRunAdminFailed(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The installer's administrator password could not be removed from {File}. " +
                  "Delete the FirstRun section from that file by hand.")]
    public static partial void FirstRunPasswordNotCleared(ILogger logger, string file);
}
