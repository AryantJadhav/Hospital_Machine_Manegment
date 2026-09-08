namespace HospitalPm.Domain.Updates;

/// <summary>
/// What a signed update file says about the installer sitting next to it.
///
/// Small on purpose. The installer is a couple of hundred megabytes and
/// travels on a USB stick; this is the part that gets signed, and it fits in
/// an email if it ever needs to.
/// </summary>
/// <param name="Version">Strictly x.y.z, matching the installer's own version resource.</param>
/// <param name="InstallerFileName">
/// A bare file name, never a path. The installer must be in the same folder
/// as the manifest — a manifest that could name a path would let a signed
/// file from one release point at an executable from anywhere on the machine.
/// </param>
/// <param name="Sha256">Lowercase hex of the installer's SHA-256.</param>
/// <param name="SizeBytes">Checked before hashing, so an obviously wrong file fails in milliseconds.</param>
/// <param name="ReleasedOn">For the person deciding whether to install it.</param>
/// <param name="Notes">What changed, in a sentence or two. Shown on the update page.</param>
public sealed record UpdateManifest(
    string Version,
    string InstallerFileName,
    string Sha256,
    long SizeBytes,
    DateOnly ReleasedOn,
    string? Notes);
