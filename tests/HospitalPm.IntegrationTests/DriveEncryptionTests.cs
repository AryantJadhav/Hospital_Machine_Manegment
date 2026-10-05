using HospitalPm.Infrastructure.Operations;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Reading what the operating system says about the drive. The parsing is checked against real output; asking the real
/// system is not, because what it answers depends on the machine the tests run on.
/// </summary>
public sealed class DriveEncryptionTests
{
    private const string Encrypted = """
        BitLocker Drive Encryption: Configuration Tool version 10.0.26100
        Copyright (C) 2013 Microsoft Corporation. All rights reserved.

        Volume C: [OS]
        [OS Volume]

            Size:                 475.34 GB
            BitLocker Version:    2.0
            Conversion Status:    Fully Encrypted
            Percentage Encrypted: 100.0%
            Encryption Method:    XTS-AES 128
            Protection Status:    Protection On
            Lock Status:          Unlocked
        """;

    [Fact]
    public void A_drive_with_bitlocker_protection_on_is_encrypted()
    {
        var result = DriveEncryption.ParseBitLocker(0, Encrypted, "C:");

        Assert.True(result.Encrypted);
        Assert.Contains("BitLocker", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drive_that_was_never_encrypted_is_not()
    {
        var plain = Encrypted
            .Replace("Fully Encrypted", "Fully Decrypted", StringComparison.Ordinal)
            .Replace("Protection On", "Protection Off", StringComparison.Ordinal);

        Assert.False(DriveEncryption.ParseBitLocker(0, plain, "C:").Encrypted);
    }

    [Fact]
    public void A_drive_whose_protection_is_suspended_is_not_called_protected()
    {
        // Encrypted, but the key is in the clear while it is suspended: not something to pass.
        var suspended = Encrypted.Replace("Protection On", "Protection Off", StringComparison.Ordinal);

        Assert.False(DriveEncryption.ParseBitLocker(0, suspended, "C:").Encrypted);
    }

    [Theory]
    [InlineData(1, "ERROR: An attempt to access a required resource was denied.")]
    [InlineData(0, "")]
    [InlineData(0, "something this check has never seen")]
    [InlineData(-1, "")]
    public void When_the_system_will_not_say_the_answer_is_not_known_rather_than_a_guess(int exit, string output)
    {
        var result = DriveEncryption.ParseBitLocker(exit, output, "C:");

        Assert.Null(result.Encrypted);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public void A_linux_volume_on_a_luks_layer_is_encrypted()
    {
        Assert.True(DriveEncryption.ParseLsblk(0, "lvm\ncrypt\npart\ndisk\n", "/dev/mapper/vg-root").Encrypted);
    }

    [Fact]
    public void A_linux_volume_with_no_crypt_layer_is_not()
    {
        Assert.False(DriveEncryption.ParseLsblk(0, "part\ndisk\n", "/dev/sda2").Encrypted);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "  \n")]
    public void When_the_layers_cannot_be_listed_the_answer_is_not_known(int exit, string output)
    {
        Assert.Null(DriveEncryption.ParseLsblk(exit, output, "/dev/sda2").Encrypted);
    }

    [Fact]
    public void Asking_the_real_system_never_throws()
    {
        // Whatever this machine says, the check returns an answer instead of failing the Diagnostics page.
        var result = DriveEncryption.Check(Path.GetTempPath());

        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }
}
