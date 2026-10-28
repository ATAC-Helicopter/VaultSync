#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;
using Responses = VaultSync.Core.Services.LinuxRecoveryResponses;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryResponsesTests
{
    private static readonly Guid OperationId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ObservationId = Guid.Parse("11112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ReviewId = Guid.Parse("22112233-4455-6677-8899-aabbccddeeff");
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch;
    private const ulong Total = 128 * 1024 + 512;
    private static Responses.Device Disk => new("sda", null, 8, 0, Total, 17, 512, 4096,
        LinuxBlockKind.WholeDisk, LinuxBlockIdentity.UniqueInSnapshot, LinuxBlockUse.Unknown, false, false, false, false);
    private static Responses.Inventory Snapshot(params Responses.Device[] devices) => new(ObservationId, Observed,
        LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly, false, Array.AsReadOnly(devices));
    private static Responses.Review Review => new(OperationId, ReviewId, Guid.Parse("33112233-4455-6677-8899-aabbccddeeff"),
        Guid.Parse("44112233-4455-6677-8899-aabbccddeeff"), 1, new string('a', 64), Observed, Total, Disk, ObservationId);
    private static Responses.Outcome Outcome(RawImageDeviceRestoreState state, LinuxRawRestoreOperationReason reason,
        RawImageDeviceRestoreResult? result, bool journal = true, bool cleanup = true) => new(ReviewId, new(state, reason, result, journal, cleanup));
    private static byte[] InventoryWire => Responses.EncodeInventory(Snapshot(Disk));
    private static byte[] ReviewWire => Responses.EncodeReview(Review);
    private static Responses.Review ReadReview(byte[] wire) => Responses.DecodeReview(wire, OperationId, Snapshot(Disk), ObservationId, "sda");

    [Fact]
    public void InventoryHasIndependentFixedFieldLayoutAndRedactsTechnicalText()
    {
        var native = new LinuxBlockObservation("sda", "8:0", null, LinuxBlockKind.WholeDisk, Total, 512, 4096,
            17, false, "serial:private", LinuxBlockIdentity.UniqueInSnapshot, false, false, false, LinuxBlockUse.Unknown);
        var projection = Responses.ProjectInventory(new(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
            ObservationId, Observed, Guid.NewGuid(), "mnt:[1234]", false, new[] { native }));
        byte[] bytes = Responses.EncodeInventory(projection);
        Assert.Equal(200, bytes.Length);
        Assert.Equal("11112233445566778899AABBCCDDEEFF", Convert.ToHexString(bytes[..16]));
        Assert.Equal(Observed.Ticks, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 }, bytes[24..32]);
        Assert.Equal(new byte[] { (byte)'s', (byte)'d', (byte)'a', 0 }, bytes[32..36]);
        Assert.Equal(8U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(160)));
        Assert.Equal(Total, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(168)));
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }, bytes[192..]);
        Assert.DoesNotContain("private", System.Text.Encoding.ASCII.GetString(bytes));
        var decoded = Responses.DecodeInventory(bytes);
        Assert.Equal(projection.ObservationId, decoded.ObservationId); Assert.Equal(Disk, Assert.Single(decoded.Devices));
    }

    [Fact]
    public void IndependentlyConstructedBodiesDecodeAtDocumentedOffsets()
    {
        byte[] inventory = new byte[200];
        // Header: RFC UUID, Unix epoch in UTC ticks, observed/namespace/flags/reserved, count=1.
        Convert.FromHexString("11112233445566778899aabbccddeeff0080b5f7f57f9f080000000001000000").CopyTo(inventory, 0);
        inventory[32] = (byte)'s'; inventory[33] = (byte)'d'; inventory[34] = (byte)'a';
        // Device record starts at32: major at128, minor132, capacity136, diskseq144,
        // logical152, physical156, kind/identity/use/flags160, reserved164.
        Convert.FromHexString("08000000000000000002020000000000110000000000000000020000001000000001000000000000").CopyTo(inventory, 160);
        Assert.Equal(Disk, Assert.Single(Responses.DecodeInventory(inventory).Devices));
        Assert.Equal(InventoryWire, inventory);
        byte[] progress = Convert.FromHexString("22112233445566778899aabbccddeeff0000020000000000");
        Assert.Equal(new Responses.Progress(ReviewId, 131072), Responses.DecodeProgress(progress, Review));
        // Full successful body, including all reserved bytes and exact final non-chunk total.
        byte[] outcome = Convert.FromHexString(
            "22112233445566778899aabbccddeeff030b030f00000000" +
            "000202000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000");
        var expected = Outcome(RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified,
            new(RawImageDeviceRestoreState.WrittenAndReadBack, Total, 0, 0, true));
        Assert.Equal(expected, Responses.DecodeOutcome(outcome, Review));
        Assert.Equal(outcome, Responses.EncodeOutcome(expected, Review));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 2)] [InlineData(2, 3)] [InlineData(2, 4)] [InlineData(3, 5)]
    public void NativeFailureSnapshotsRemainTypedAndEmpty(int state, int reason)
    {
        var value = new Responses.Inventory(Guid.Empty, Observed, (LinuxBlockInventoryState)state,
            (LinuxBlockInventoryReason)reason, false, Array.Empty<Responses.Device>());
        var decoded = Responses.DecodeInventory(Responses.EncodeInventory(value));
        Assert.Equal(value.State, decoded.State); Assert.Equal(value.Reason, decoded.Reason); Assert.Empty(decoded.Devices);
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { ObservationId = ObservationId }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { Devices = new[] { Disk } }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { HasUnresolvedSwaps = true }));
    }

    [Fact]
    public void EmptyZeroCapacityAndUnresolvedObservedSnapshotsAreNotRestoreCandidates()
    {
        Assert.Empty(Responses.DecodeInventory(Responses.EncodeInventory(Snapshot())).Devices);
        var value = Snapshot(Disk with { Capacity = 0, DiskSequence = null }) with { HasUnresolvedSwaps = true };
        var decoded = Responses.DecodeInventory(Responses.EncodeInventory(value));
        Assert.True(decoded.HasUnresolvedSwaps); Assert.Equal(0UL, Assert.Single(decoded.Devices).Capacity);
        Assert.Throws<InvalidDataException>(() => Responses.EncodeReview(Review with { Target = value.Devices[0], OverwriteLength = 0 }));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(ReviewWire, OperationId,
            Snapshot(Disk) with { HasUnresolvedSwaps = true }, ObservationId, "sda"));
    }

    [Fact]
    public void CountsAndBodyLengthsAreCheckedBeforeUntrustedAllocation()
    {
        byte[] bytes = InventoryWire;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => Responses.DecodeInventory(bytes));
        for (int length = 0; length < InventoryWire.Length; length++)
        {
            byte[] truncated = InventoryWire[..length];
            Assert.Throws<InvalidDataException>(() => Responses.DecodeInventory(truncated));
        }
        Assert.Throws<InvalidDataException>(() => Responses.DecodeInventory(InventoryWire.Concat(new byte[] { 0 }).ToArray()));
        var maximum = Snapshot(Enumerable.Range(0, 256).Select(i => Disk with { KernelName = "sd" + i, Minor = (uint)i }).ToArray());
        Assert.Equal(43040, Responses.EncodeInventory(maximum).Length);
        Assert.Equal(256, Responses.DecodeInventory(Responses.EncodeInventory(maximum)).Devices.Count);
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(maximum with { Devices = maximum.Devices.Append(Disk).ToArray() }));
    }

    [Theory]
    [InlineData(24, 255)] [InlineData(25, 255)] [InlineData(26, 2)] [InlineData(27, 1)]
    [InlineData(35, 128)] [InlineData(95, 1)] [InlineData(96, 1)]
    [InlineData(192, 255)] [InlineData(193, 255)] [InlineData(194, 255)] [InlineData(195, 128)] [InlineData(196, 1)]
    public void InventoryRejectsUnknownEnumsFlagsReservedAndHiddenSelectors(int offset, byte value)
    {
        byte[] bytes = InventoryWire; bytes[offset] = value;
        Assert.Throws<InvalidDataException>(() => Responses.DecodeInventory(bytes));
    }

    [Fact]
    public void InventoryRejectsInvalidTimestampIdsAndStatePairs()
    {
        var value = Snapshot(Disk);
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { ObservationId = Guid.Empty }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { ObservedAtUtc = Observed.AddTicks(-1) }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { ObservedAtUtc = Observed.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(value with { Reason = LinuxBlockInventoryReason.MetadataChanged }));
        byte[] bytes = InventoryWire; BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), long.MaxValue);
        Assert.Throws<InvalidDataException>(() => Responses.DecodeInventory(bytes));
    }

    public static IEnumerable<object[]> InvalidDevices()
    {
        yield return new object[] { Disk with { KernelName = "../sda" } };
        yield return new object[] { Disk with { KernelName = new string('x', 64) } };
        yield return new object[] { Disk with { KernelName = "sdá" } };
        yield return new object[] { Disk with { ParentKernelName = "sdb" } };
        yield return new object[] { Disk with { Kind = LinuxBlockKind.Partition } };
        yield return new object[] { Disk with { Kind = LinuxBlockKind.VirtualOrStacked } };
        yield return new object[] { Disk with { LogicalSectorBytes = 513 } };
        yield return new object[] { Disk with { PhysicalSectorBytes = 256 } };
        yield return new object[] { Disk with { PhysicalSectorBytes = 2097152 } };
        yield return new object[] { Disk with { Capacity = Total + 1 } };
        yield return new object[] { Disk with { DiskSequence = 0 } };
        yield return new object[] { Disk with { MountObserved = true } };
        yield return new object[] { Disk with { SwapObserved = true } };
        yield return new object[] { Disk with { HasHolders = true } };
    }
    [Theory] [MemberData(nameof(InvalidDevices))]
    public void InvalidNativeGeometryIdentityAndUseCannotBeSerialized(object device) =>
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(Snapshot((Responses.Device)device)));

    [Fact]
    public void InventoryPreservesParentGeometrySequenceAndUseIncludingVirtualParents()
    {
        var partition = Disk with { KernelName = "sda1", Minor = 1, Kind = LinuxBlockKind.Partition,
            ParentKernelName = "sda", Identity = LinuxBlockIdentity.Unknown };
        Assert.Equal(2, Responses.DecodeInventory(Responses.EncodeInventory(Snapshot(partition, Disk))).Devices.Count);
        var parent = Disk with { Kind = LinuxBlockKind.VirtualOrStacked, Identity = LinuxBlockIdentity.Unknown };
        Assert.Equal(parent, Responses.DecodeInventory(Responses.EncodeInventory(Snapshot(parent, partition))).Devices[0]);
        foreach (var changed in new[] { partition with { ParentKernelName = "missing" }, partition with { DiskSequence = 18 },
            partition with { LogicalSectorBytes = 4096, Capacity = 0 }, partition with { Capacity = Total + 512 },
            partition with { Identity = LinuxBlockIdentity.UniqueInSnapshot }, partition with { Use = LinuxBlockUse.ObservedInUse } })
            Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(Snapshot(Disk, changed)));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(Snapshot(Disk, Disk with { Minor = 1 })));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeInventory(Snapshot(Disk, Disk with { KernelName = "sdb" })));
    }

    [Fact]
    public void ReviewHasExactNetworkIdsHashAndTargetBinding()
    {
        byte[] bytes = ReviewWire;
        Assert.Equal(304, bytes.Length);
        Assert.Equal("00112233445566778899AABBCCDDEEFF22112233445566778899AABBCCDDEEFF", Convert.ToHexString(bytes[..32]));
        Assert.Equal(Enumerable.Repeat((byte)0xaa, 32), bytes[72..104]);
        Assert.Equal(Review, ReadReview(bytes));
        Guid newerObservation = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(bytes, OperationId,
            Snapshot(Disk) with { ObservationId = newerObservation }, newerObservation, "sda"));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(bytes, Guid.NewGuid(), Snapshot(Disk), ObservationId, "sda"));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(bytes, OperationId, Snapshot(Disk), Guid.NewGuid(), "sda"));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(bytes, OperationId, Snapshot(Disk), ObservationId, "sdb"));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeReview(bytes, OperationId, Snapshot(Disk with { DiskSequence = 18 }), ObservationId, "sda"));
        for (int i = 0; i < bytes.Length; i++)
        { byte[] truncated = bytes[..i]; Assert.Throws<InvalidDataException>(() => ReadReview(truncated)); }
        Assert.Throws<InvalidDataException>(() => ReadReview(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void ReviewRejectsInvalidIdsManifestAndUnsafeTargets()
    {
        foreach (var changed in new[] { Review with { OperationId = Guid.Empty }, Review with { ReviewId = Guid.Empty },
            Review with { ImageId = Guid.Empty }, Review with { CaptureId = Guid.Empty }, Review with { ObservationId = Guid.Empty }, Review with { Generation = 0 },
            Review with { ManifestRoot = new string('A', 64) }, Review with { ManifestRoot = "bad" },
            Review with { OverwriteLength = Total - 512 }, Review with { OverwriteLength = 0 },
            Review with { Target = Disk with { IsReadOnly = true } }, Review with { Target = Disk with { DiskSequence = null } },
            Review with { Target = Disk with { Identity = LinuxBlockIdentity.Ambiguous } },
            Review with { Target = Disk with { Use = LinuxBlockUse.ObservedInUse } },
            Review with { Target = Disk with { PhysicalSectorBytes = 8192 } },
            Review with { Target = Disk with { Capacity = Responses.MaximumRestoreBytes + 512 }, OverwriteLength = Responses.MaximumRestoreBytes + 512 } })
            Assert.Throws<InvalidDataException>(() => Responses.EncodeReview(changed));
    }

    [Fact]
    public void ReviewWireRejectsEmptyImageIdentityAndReservedTargetBytes()
    {
        foreach (int offset in new[] { 0, 16, 32, 48, 288 })
        {
            byte[] bytes = ReviewWire; Array.Clear(bytes, offset, 16);
            Assert.Throws<InvalidDataException>(() => ReadReview(bytes));
        }
        byte[] reserved = ReviewWire; reserved[287] = 1;
        Assert.Throws<InvalidDataException>(() => ReadReview(reserved));
        byte[] generation = ReviewWire; Array.Clear(generation, 64, 8);
        Assert.Throws<InvalidDataException>(() => ReadReview(generation));
    }

    [Fact]
    public void ProgressCarriesOnlyDurableBytesAndRequiresExactReviewAndMonotonicContext()
    {
        foreach (ulong bytes in new ulong[] { 0, 128 * 1024, Total })
        {
            var progress = new Responses.Progress(ReviewId, bytes);
            Assert.Equal(progress, Responses.DecodeProgress(Responses.EncodeProgress(progress, Review), Review));
        }
        byte[] wire = Responses.EncodeProgress(new(ReviewId, 128 * 1024), Review);
        Assert.Equal("22112233445566778899AABBCCDDEEFF0000020000000000", Convert.ToHexString(wire));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeProgress(wire, Review, Total));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeProgress(wire, Review, 512));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeProgress(wire, Review with { ReviewId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeProgress(new(ReviewId, 512), Review));
        Assert.Throws<InvalidDataException>(() => Responses.EncodeProgress(new(ReviewId, Total + 512), Review));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeProgress(wire[..23], Review));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeProgress(wire.Concat(new byte[] { 0 }).ToArray(), Review));
    }

    [Fact]
    public void OutcomePreservesUncertaintyFullAcknowledgmentFailureAndCleanupFailure()
    {
        var successful = new RawImageDeviceRestoreResult(RawImageDeviceRestoreState.WrittenAndReadBack, Total, 0, 0, true);
        var cases = new[] {
            Outcome(RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified, successful),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed,
                successful with { State = RawImageDeviceRestoreState.Failed }),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.CleanupUnconfirmed, successful, cleanup: false),
            Outcome(RawImageDeviceRestoreState.Interrupted, LinuxRawRestoreOperationReason.TimedOut,
                new(RawImageDeviceRestoreState.Interrupted, 128 * 1024, 128 * 1024, 512, false)),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed,
                new(RawImageDeviceRestoreState.Failed, 0, 0, 128 * 1024, true)),
            Outcome(RawImageDeviceRestoreState.Interrupted, LinuxRawRestoreOperationReason.Interrupted, null, journal: false),
            Outcome(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.TargetUnavailable, null),
            Outcome(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.ExecutionRefused,
                new(RawImageDeviceRestoreState.Refused, 0, 0, 0, false)),
            Outcome(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.AlreadyConsumed, null, journal: false, cleanup: false)
        };
        foreach (var value in cases)
            Assert.Equal(value, Responses.DecodeOutcome(Responses.EncodeOutcome(value, Review), Review));
        var wire = Responses.EncodeOutcome(cases[0], Review);
        Assert.Equal(new byte[] { 3, 11, 3, 15, 0, 0, 0, 0 }, wire[16..24]);
        Assert.Equal(cases[1], Responses.DecodeOutcome(Responses.EncodeOutcome(cases[1], Review, Total), Review, Total));
    }

    [Fact]
    public void ImpossibleOutcomesCannotLoseUncertaintyOrClaimSuccess()
    {
        var success = Outcome(RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified,
            new(RawImageDeviceRestoreState.WrittenAndReadBack, Total, 0, 0, true));
        foreach (var invalid in new[] {
            success with { ReviewId = Guid.NewGuid() },
            success with { Execution = success.Execution with { State = (RawImageDeviceRestoreState)99 } },
            success with { Execution = success.Execution with { Reason = LinuxRawRestoreOperationReason.Failed } },
            success with { Execution = success.Execution with { JournalAcquired = false } },
            success with { Execution = success.Execution with { CleanupConfirmed = false } },
            success with { Execution = success.Execution with { WriteResult = null } },
            success with { Execution = success.Execution with { WriteResult = success.Execution.WriteResult! with { TerminalRecordWritten = false } } },
            success with { Execution = success.Execution with { WriteResult = success.Execution.WriteResult! with { AcknowledgedBytes = 0 } } },
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.CleanupUnconfirmed, null, cleanup: true),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, new((RawImageDeviceRestoreState)99, 0, 0, 0, true)),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, new(RawImageDeviceRestoreState.Failed, 512, 0, 0, true)),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, new(RawImageDeviceRestoreState.Failed, 0, 512, 0, true)),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, new(RawImageDeviceRestoreState.Failed, 0, 0, 512, true)),
            Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, new(RawImageDeviceRestoreState.Failed, Total, Total, 512, true)),
            Outcome(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.ReviewRefused, new(RawImageDeviceRestoreState.Refused, 0, 0, 0, false)),
            Outcome(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.AlreadyConsumed, null)
        }) Assert.Throws<InvalidDataException>(() => Responses.EncodeOutcome(invalid, Review));
        var missing = Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, null);
        Assert.Throws<InvalidDataException>(() => Responses.EncodeOutcome(missing, Review, 128 * 1024));
    }

    [Theory]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)] [InlineData(48)] [InlineData(63)]
    public void OutcomeRejectsUnknownEnumsFlagsAndEveryReservedArea(int offset)
    {
        byte[] bytes = Responses.EncodeOutcome(Outcome(RawImageDeviceRestoreState.Failed,
            LinuxRawRestoreOperationReason.Failed, new(RawImageDeviceRestoreState.Failed, 0, 0, 0, false)), Review);
        bytes[offset] = 255;
        Assert.Throws<InvalidDataException>(() => Responses.DecodeOutcome(bytes, Review));
    }

    [Fact]
    public void AbsentWriterCannotSmuggleStatsOrFlagsAndOutcomeLengthIsExact()
    {
        byte[] wire = Responses.EncodeOutcome(Outcome(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, null), Review);
        foreach (int at in new[] { 18, 24, 32, 40 })
        { byte[] mutated = (byte[])wire.Clone(); mutated[at] = 1; Assert.Throws<InvalidDataException>(() => Responses.DecodeOutcome(mutated, Review)); }
        wire[19] |= 8; Assert.Throws<InvalidDataException>(() => Responses.DecodeOutcome(wire, Review));
        for (int length = 0; length < 64; length++)
        { byte[] truncated = wire[..length]; Assert.Throws<InvalidDataException>(() => Responses.DecodeOutcome(truncated, Review)); }
        Assert.Throws<InvalidDataException>(() => Responses.DecodeOutcome(new byte[65], Review));
    }

    [Fact]
    public void RefusedIsAnExplicitPreExecutionAllowlist()
    {
        foreach (var reason in new[] { LinuxRawRestoreOperationReason.ReviewRefused, LinuxRawRestoreOperationReason.ImageChanged,
            LinuxRawRestoreOperationReason.SelectionChanged, LinuxRawRestoreOperationReason.JournalUnavailable,
            LinuxRawRestoreOperationReason.TargetUnavailable, LinuxRawRestoreOperationReason.AlreadyConsumed })
            Assert.Equal(reason, Responses.DecodeRefused(Responses.EncodeRefused(reason)));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, Responses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused));
        foreach (var reason in new[] { LinuxRawRestoreOperationReason.ReviewReady, LinuxRawRestoreOperationReason.Interrupted,
            LinuxRawRestoreOperationReason.TimedOut, LinuxRawRestoreOperationReason.Failed, LinuxRawRestoreOperationReason.CleanupUnconfirmed,
            LinuxRawRestoreOperationReason.BytesVerified, LinuxRawRestoreOperationReason.ExecutionRefused, (LinuxRawRestoreOperationReason)256 })
        {
            Assert.Throws<InvalidDataException>(() => Responses.EncodeRefused(reason));
            byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)reason);
            Assert.Throws<InvalidDataException>(() => Responses.DecodeRefused(bytes));
        }
        Assert.Throws<InvalidDataException>(() => Responses.DecodeRefused(new byte[3]));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeRefused(new byte[5]));
        Assert.Throws<InvalidDataException>(() => Responses.DecodeRefused(new byte[] { 255, 255, 255, 255 }));
    }
}
