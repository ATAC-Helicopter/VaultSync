#nullable enable
using System;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskRestorePreviewServiceTests
{
    private static readonly Guid Operation = Guid.NewGuid();
    private static readonly DiskRestoreImageDescriptor Image = new(Guid.NewGuid(), 1,
        new string('a', 64), 8192, 512, Guid.NewGuid());
    private static readonly DiskRestoreDeviceObservation Target = new(Guid.NewGuid(), Guid.NewGuid(),
        "fixture-hardware-identity", 8192, 512, DiskRestoreDeviceKind.PhysicalDisk,
        DiskRestoreDeviceUse.Offline, DiskRestoreIdentity.Unique, false);

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void ExactSizeOfflineTargetProducesOnlyWholeDiskPreview(int sector)
    {
        var image = Image with { LogicalSectorBytes = (uint)sector };
        var target = Target with { LogicalSectorBytes = (uint)sector };
        var result = DiskRestorePreviewService.Create(Operation, image, target);
        Assert.Equal(DiskRestorePreviewReason.PreviewOnly, result.Reason);
        Assert.NotNull(result.Preview);
        Assert.Equal(0UL, result.Preview.OverwriteOffset);
        Assert.Equal(image.Capacity, result.Preview.OverwriteLength);
        Assert.Equal(image, result.Preview.Image);
        Assert.Equal(target, result.Preview.Target);
        Assert.Equal(DiskRestorePreviewReason.PreviewOnly,
            DiskRestorePreviewService.Recheck(result.Preview, Operation, image, target with { }).Reason);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty-id")]
    [InlineData("generation")]
    [InlineData("root")]
    [InlineData("root-length")]
    [InlineData("root-null")]
    [InlineData("root-uppercase")]
    [InlineData("zero-capacity")]
    [InlineData("alignment")]
    [InlineData("sector")]
    public void IncompleteOrUnsupportedImageDoesNotProducePreview(string defect)
    {
        var image = defect switch
        {
            "null" => null,
            "empty-id" => Image with { ImageId = Guid.Empty },
            "generation" => Image with { Generation = 0 },
            "root" => Image with { ManifestRoot = new string('g', 64) },
            "root-length" => Image with { ManifestRoot = "abcd" },
            "root-null" => Image with { ManifestRoot = null! },
            "root-uppercase" => Image with { ManifestRoot = new string('A', 64) },
            "zero-capacity" => Image with { Capacity = 0 },
            "alignment" => Image with { Capacity = ulong.MaxValue },
            _ => Image with { LogicalSectorBytes = 1024 }
        };
        AssertRefused(image, Target, DiskRestorePreviewReason.InvalidImage);
    }

    [Fact]
    public void UnknownOperationAndRepositoryCannotBePlanned()
    {
        var result = DiskRestorePreviewService.Create(Guid.Empty, Image, Target);
        Assert.Equal(DiskRestorePreviewReason.InvalidOperation, result.Reason);
        Assert.Null(result.Preview);
        AssertRefused(Image with { RepositoryDeviceId = Guid.Empty }, Target,
            DiskRestorePreviewReason.UnknownRepositoryDevice);
        AssertRefused(Image with { RepositoryDeviceId = Target.DeviceId }, Target,
            DiskRestorePreviewReason.RepositoryOnTarget);
    }

    [Theory]
    [InlineData("null", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("empty-id", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("generation", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("identity-null", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("identity-blank", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("identity-control", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("identity-large", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("zero-capacity", DiskRestorePreviewReason.InvalidDevice)]
    [InlineData("file", DiskRestorePreviewReason.UnsupportedDevice)]
    [InlineData("kind-unknown", DiskRestorePreviewReason.UnsupportedDevice)]
    [InlineData("identity-unknown", DiskRestorePreviewReason.UncertainIdentity)]
    [InlineData("ambiguous", DiskRestorePreviewReason.UncertainIdentity)]
    [InlineData("mounted", DiskRestorePreviewReason.DeviceNotOffline)]
    [InlineData("use-unknown", DiskRestorePreviewReason.DeviceNotOffline)]
    [InlineData("readonly", DiskRestorePreviewReason.DeviceNotWritable)]
    [InlineData("writable-unknown", DiskRestorePreviewReason.DeviceNotWritable)]
    [InlineData("sector", DiskRestorePreviewReason.UnsupportedGeometry)]
    [InlineData("sector-mismatch", DiskRestorePreviewReason.UnsupportedGeometry)]
    [InlineData("alignment", DiskRestorePreviewReason.UnsupportedGeometry)]
    [InlineData("smaller", DiskRestorePreviewReason.TargetTooSmall)]
    [InlineData("larger", DiskRestorePreviewReason.LargerTargetUnqualified)]
    public void UnsafeOrUnknownTargetNeverProducesPreview(string defect, DiskRestorePreviewReason reason)
    {
        var target = defect switch
        {
            "null" => null,
            "empty-id" => Target with { DeviceId = Guid.Empty },
            "generation" => Target with { DeviceGeneration = Guid.Empty },
            "identity-null" => Target with { HardwareIdentity = null! },
            "identity-blank" => Target with { HardwareIdentity = " " },
            "identity-control" => Target with { HardwareIdentity = "id\n" },
            "identity-large" => Target with { HardwareIdentity = new string('x', 257) },
            "zero-capacity" => Target with { Capacity = 0 },
            "file" => Target with { Kind = DiskRestoreDeviceKind.RegularFile },
            "kind-unknown" => Target with { Kind = (DiskRestoreDeviceKind)99 },
            "identity-unknown" => Target with { Identity = DiskRestoreIdentity.Unknown },
            "ambiguous" => Target with { Identity = DiskRestoreIdentity.Ambiguous },
            "mounted" => Target with { Use = DiskRestoreDeviceUse.InUse },
            "use-unknown" => Target with { Use = (DiskRestoreDeviceUse)99 },
            "readonly" => Target with { IsReadOnly = true },
            "writable-unknown" => Target with { IsReadOnly = null },
            "sector" => Target with { LogicalSectorBytes = 0 },
            "sector-mismatch" => Target with { LogicalSectorBytes = 4096 },
            "alignment" => Target with { Capacity = ulong.MaxValue },
            "smaller" => Target with { Capacity = 4096 },
            _ => Target with { Capacity = 16384 }
        };
        AssertRefused(Image, target, reason);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("image")]
    [InlineData("image-generation")]
    [InlineData("manifest")]
    [InlineData("repository")]
    [InlineData("target")]
    [InlineData("target-generation")]
    [InlineData("hardware")]
    [InlineData("capacity")]
    [InlineData("sector")]
    public void ChangedSelectionInvalidatesPriorPreviewEvenIfNewSelectionIsSafe(string change)
    {
        var preview = DiskRestorePreviewService.Create(Operation, Image, Target).Preview;
        var operation = change == "operation" ? Guid.NewGuid() : Operation;
        var image = change switch
        {
            "image" => Image with { ImageId = Guid.NewGuid() },
            "image-generation" => Image with { Generation = 2 },
            "manifest" => Image with { ManifestRoot = new string('b', 64) },
            "repository" => Image with { RepositoryDeviceId = Guid.NewGuid() },
            "capacity" => Image with { Capacity = 16384 },
            "sector" => Image with { LogicalSectorBytes = 4096 },
            _ => Image
        };
        var target = change switch
        {
            "target" => Target with { DeviceId = Guid.NewGuid() },
            "target-generation" => Target with { DeviceGeneration = Guid.NewGuid() },
            "hardware" => Target with { HardwareIdentity = "replacement" },
            "capacity" => Target with { Capacity = 16384 },
            "sector" => Target with { LogicalSectorBytes = 4096 },
            _ => Target
        };
        var result = DiskRestorePreviewService.Recheck(preview, operation, image, target);
        Assert.Equal(DiskRestorePreviewReason.PreviewChanged, result.Reason);
        Assert.Null(result.Preview);
    }

    [Fact]
    public void RecheckRejectsNewlyUnsafeStateAndNeverInventsMissingPreview()
    {
        var preview = DiskRestorePreviewService.Create(Operation, Image, Target).Preview;
        var result = DiskRestorePreviewService.Recheck(preview, Operation, Image,
            Target with { Use = DiskRestoreDeviceUse.InUse });
        Assert.Equal(DiskRestorePreviewReason.DeviceNotOffline, result.Reason);
        Assert.Null(result.Preview);
        Assert.Equal(DiskRestorePreviewReason.PreviewMissing,
            DiskRestorePreviewService.Recheck(null, Operation, Image, Target).Reason);
    }

    [Fact]
    public void MaximumAlignedCapacityDoesNotOverflowOverwriteRange()
    {
        const ulong capacity = ulong.MaxValue - 511;
        var result = DiskRestorePreviewService.Create(Operation,
            Image with { Capacity = capacity }, Target with { Capacity = capacity });
        Assert.Equal(DiskRestorePreviewReason.PreviewOnly, result.Reason);
        Assert.Equal(capacity, result.Preview!.OverwriteLength);
    }

    private static void AssertRefused(DiskRestoreImageDescriptor? image,
        DiskRestoreDeviceObservation? target, DiskRestorePreviewReason reason)
    {
        var result = DiskRestorePreviewService.Create(Operation, image, target);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Preview);
    }
}
