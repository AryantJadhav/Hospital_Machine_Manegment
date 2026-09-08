using System.Security.Cryptography;
using HospitalPm.Domain.Updates;

namespace HospitalPm.Infrastructure.Updates;

/// <summary>
/// The real disk.
///
/// Hashing is streamed rather than read into memory: the installer bundles
/// PostgreSQL and is a couple of hundred megabytes, and a hospital PC with
/// 4 GB of RAM running the database as well should not have to hold all of it
/// at once.
/// </summary>
public sealed class UpdateFileSystem : IUpdateFileSystem
{
    public string Combine(string folder, string fileName) => Path.Combine(folder, fileName);

    public bool FileExists(string path) => File.Exists(path);

    public long FileSize(string path) => new FileInfo(path).Length;

    public string Sha256Hex(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 128, FileOptions.SequentialScan);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
