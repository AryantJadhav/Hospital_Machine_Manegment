using HospitalPm.Infrastructure.Operations;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A backup vault whose keys live in a folder of their own under the temp directory, so no test ever reads or
/// writes a real installation's keys. Folders are small and left to the operating system's temp cleaning.
/// </summary>
internal static class TestVault
{
    public static BackupVault Create(int chunkSize = BackupVault.DefaultChunkSize) =>
        new(NewKeyDirectory(), chunkSize: chunkSize);

    public static string NewKeyDirectory() =>
        Path.Combine(Path.GetTempPath(), "hospitalpm-test-keys", Guid.NewGuid().ToString("N"));
}
