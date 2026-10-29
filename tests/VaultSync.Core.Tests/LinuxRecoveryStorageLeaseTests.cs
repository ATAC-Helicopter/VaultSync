#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryStorageLeaseTests
{
    private static readonly LinuxRecoveryNative.Identity Parent = new(0x41c0, 0, 0, 2, 10, 0, 8, 1, 0, 0, 0, 0);
    private static Dictionary<string, LinuxRecoveryNative.Identity> Files() => new(StringComparer.Ordinal)
    {
        ["image.metadata"] = new(0x8100, 0, 0, 1, 11, 596, 8, 1, 0, 0, 0, 0),
        ["payload.raw"] = new(0x8100, 0, 0, 1, 12, 65536, 8, 1, 0, 0, 0, 0)
    };

    [Fact]
    public void OnlyExactSealedOwnedFilesCanBecomeDeletionPlan()
    {
        LinuxRecoveryStorageLease.ValidateStagingFiles(Files(), Parent, 65536);
        foreach (string name in new[] { "image.metadata", "payload.raw" })
        {
            var files = Files(); files.Remove(name);
            Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(files, Parent, 65536));
        }
        var extra = Files(); extra["personal.backup"] = extra["payload.raw"];
        Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(extra, Parent, 65536));
    }

    [Fact]
    public void HardlinksAliasesOtherOwnersDevicesAndWritableFilesRefuseBeforeUnlink()
    {
        var baseFile = Files()["payload.raw"];
        foreach (var changed in new[]
        {
            baseFile with { Links = 2 }, baseFile with { Owner = 60999 }, baseFile with { DeviceMinor = 2 },
            baseFile with { Mode = 0x8180 }, baseFile with { Mode = 0xa100 }, baseFile with { Size = 65535 },
            baseFile with { Inode = 11 }
        })
        {
            var files = Files(); files["payload.raw"] = changed;
            Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(files, Parent, 65536));
        }
    }

    [Theory]
    [InlineData(LinuxRecoverySessionPhase.Faulted)] [InlineData(LinuxRecoverySessionPhase.Refused)]
    [InlineData(LinuxRecoverySessionPhase.Executing)] [InlineData(LinuxRecoverySessionPhase.Completed)]
    public void MissingDurableOutcomeAlwaysRetainsEvidence(object phase)
    {
        var state = new LinuxRecoverySession.Snapshot((LinuxRecoverySessionPhase)phase, false, null, null, 0, null, null);
        Assert.False(LinuxRecoveryStorageLease.Successful(state));
    }

    [Fact]
    public void OnlyCompleteDurableReadbackAndCleanupPermitsStagingRemoval()
    {
        Guid reviewId = Guid.NewGuid();
        var target = new LinuxRecoveryResponses.Device("fixture-disk", null, 1, 1, 65536, 1, 512, 512,
            Models.LinuxBlockKind.WholeDisk, Models.LinuxBlockIdentity.UniqueInSnapshot, Models.LinuxBlockUse.Unknown, false, false, false, false);
        var review = new LinuxRecoveryResponses.Review(Guid.NewGuid(), reviewId, Guid.NewGuid(), Guid.NewGuid(), 1,
            new string('a', 64), DateTimeOffset.UnixEpoch, 65536, target, Guid.NewGuid());
        var execution = new LinuxRawRestoreExecution(RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified,
            new(RawImageDeviceRestoreState.WrittenAndReadBack, 65536, 0, 0, true), true, true);
        var state = new LinuxRecoverySession.Snapshot(LinuxRecoverySessionPhase.Completed, false, null, review, 65536,
            new(reviewId, execution), null);
        Assert.True(LinuxRecoveryStorageLease.Successful(state));
        foreach (var failed in new[]
        {
            execution with { CleanupConfirmed = false }, execution with { JournalAcquired = false },
            execution with { WriteResult = null },
            execution with { State = RawImageDeviceRestoreState.Failed, Reason = LinuxRawRestoreOperationReason.Failed },
            execution with { WriteResult = execution.WriteResult! with { State = RawImageDeviceRestoreState.Failed } },
            execution with { WriteResult = execution.WriteResult! with { TerminalRecordWritten = false } },
            execution with { WriteResult = execution.WriteResult! with { AcknowledgedBytes = 0 } }
        }) Assert.False(LinuxRecoveryStorageLease.Successful(state with { Outcome = new(reviewId, failed) }));
    }
}
