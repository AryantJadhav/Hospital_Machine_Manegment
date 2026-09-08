namespace HospitalPm.Domain.Updates;

/// <summary>
/// Why an update file can or cannot be installed.
///
/// Every rejection is a separate value rather than one "invalid", because the
/// person reading it is standing at a hospital PC with a USB stick and needs
/// to know whether to re-copy the file, ask for it again, or stop.
/// </summary>
public enum UpdateState
{
    /// <summary>Signed by us, the installer is present and its hash matches, and it is newer.</summary>
    Ready = 10,

    /// <summary>Not an update file at all — wrong file, or truncated in transit.</summary>
    Unreadable = 20,

    /// <summary>
    /// The signature does not check out. Either it was edited, or it was not
    /// issued by us. Both mean the same thing: do not run it.
    /// </summary>
    NotOurs = 30,

    /// <summary>Signed, but the installer named in it is not in the same folder.</summary>
    InstallerMissing = 40,

    /// <summary>
    /// The installer is there but is not the file that was signed. A partial
    /// copy off a USB stick looks exactly like this, and so does tampering.
    /// </summary>
    InstallerAltered = 50,

    /// <summary>This version is already installed, or is older than what is running.</summary>
    NotNewer = 60,
}

/// <summary>
/// One update file, and the verdict on it.
/// </summary>
/// <param name="State">The verdict.</param>
/// <param name="Manifest">Present only once the signature has verified. Never trust it before that.</param>
/// <param name="ManifestPath">Full path to the .update file this verdict is about.</param>
/// <param name="InstallerPath">Full path to the installer, when it was found.</param>
/// <param name="Message">Plain English, for someone who is not a developer.</param>
public sealed record UpdateCandidate(
    UpdateState State,
    UpdateManifest? Manifest,
    string ManifestPath,
    string? InstallerPath,
    string Message)
{
    public bool CanInstall => State == UpdateState.Ready;
}
