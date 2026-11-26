using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Sdk;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryUsbMediaTests
{
    private static LinuxBlockObservation Disk() => new("sda", "8:0", null, LinuxBlockKind.WholeDisk,
        96 * 1024 * 1024, 512, 512, 1, false, "serial:fixture-usb", LinuxBlockIdentity.UniqueInSnapshot,
        false, false, false, LinuxBlockUse.Unknown);

    [Fact]
    public void Candidate_requires_a_currently_unused_whole_disk_with_stable_identity_and_512_byte_sectors()
    {
        var disk = Disk();
        Assert.True(LinuxRecoveryUsbMedia.Eligible(disk));
        Assert.True(LinuxRecoveryUsbMedia.Eligible(disk with
            { Identity = LinuxBlockIdentity.Unknown, HardwareIdentityCandidate = null }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { KernelName = "sda1" }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { KernelName = "vda" }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { MountObserved = true }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { SwapObserved = true }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { HasHolders = true }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { Use = LinuxBlockUse.ObservedInUse }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { DiskSequence = null }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { Identity = LinuxBlockIdentity.Ambiguous }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { IsReadOnly = true }));
        Assert.False(LinuxRecoveryUsbMedia.Eligible(disk with { LogicalSectorBytes = 4096 }));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4096)]
    [InlineData(true, 4096)]
    public async Task StagingVerifiesTheImageBeforeAnAbsentDeviceCanBeOpened(bool incorrectDigest, int growth)
    {
        if (!LinuxRecoveryUsbMedia.Supported)
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("VAULTSYNC_REQUIRE_USB_STAGING_TESTS"));
            return;
        }

        var directory = Directory.CreateTempSubdirectory("recovery-usb-staging-test-");
        try
        {
            byte[] raw = await DiskImageGptInspectionServiceTests.GoldenRecoveryUsbImageAsync();
            string input = Path.Combine(directory.FullName, "image.raw");
            await File.WriteAllBytesAsync(input, raw);
            byte[] digest = SHA256.HashData(raw);
            if (incorrectDigest) digest[0] ^= 1;
            var absent = Disk() with { KernelName = "sdzzzzzzzzzzz", Capacity = (ulong)(raw.Length + growth) };

            var error = await Assert.ThrowsAsync<IOException>(() => LinuxRecoveryUsbMedia.WriteAsync(
                input, Convert.ToHexString(digest), absent,
                (_, _) => throw new XunitException("An absent USB disk must never be opened."),
                new Progress<double>(), CancellationToken.None));

            Assert.Equal(incorrectDigest ? "recovery-usb.image-digest" : "recovery-usb.changed", error.Message);
            Assert.Equal(raw, await File.ReadAllBytesAsync(input));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Partition_names_cannot_stand_in_for_gpt_codes()
    {
        const string root = "   2  526336   6817791    3.0 GiB   8300   Linux\n";
        Assert.True(LinuxRecoveryUsbMedia.HasRecoveryPartitions("   1  2048   526335    256.0 MiB   EF00\n" + root));
        Assert.False(LinuxRecoveryUsbMedia.HasRecoveryPartitions("   1  2048   526335    256.0 MiB   0700   Name EF00\n" + root));
    }
}
