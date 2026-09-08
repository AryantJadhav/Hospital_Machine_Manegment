using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HospitalPm.Infrastructure.Updates;

/// <summary>
/// Hands a staged installer to Windows and lets go.
///
/// A separate seam from the rest of the flow because this is the one step no
/// test can run: it stops the service the test host is inside. Everything
/// before it — verification, staging, the backup, the ordering — is testable
/// against a recording implementation, which is the point of the interface.
/// </summary>
public interface IUpdateLauncher
{
    /// <summary>
    /// Starts the installer and returns immediately. The caller is about to
    /// be stopped by it.
    /// </summary>
    void Launch(string installerPath, string logPath);
}

public sealed partial class UpdateLauncher(ILogger<UpdateLauncher> logger) : IUpdateLauncher
{
    public void Launch(string installerPath, string logPath)
    {
        // /VERYSILENT because nobody is at the console — the service runs as
        // LocalSystem in session 0, where a wizard would be invisible and
        // would wait forever for a click that cannot happen.
        //
        // /LOG because when this goes wrong there is no other record. The
        // process that would have logged it is the one being replaced.
        var arguments = $"/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /LOG=\"{logPath}\"";

        Log.Starting(logger, installerPath, arguments);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Deliberately not the staging folder: Inno deletes and recreates
            // paths under the install tree, and a working directory that
            // disappears underneath a process is its own class of bug. The
            // installer's own folder is stable for as long as it runs.
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
        });

        // Not waited on, and deliberately not killed on dispose. This process
        // is about to be stopped by the thing it just started; the installer
        // has to outlive it or the hospital is left with the service down and
        // half the files replaced.
        Log.Started(logger, process?.Id ?? 0);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Handing over to the installer: {Installer} {Arguments}")]
        public static partial void Starting(ILogger logger, string installer, string arguments);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Installer started as process {ProcessId}")]
        public static partial void Started(ILogger logger, int processId);
    }
}
