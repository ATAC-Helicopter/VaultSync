using System;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageCoverageServiceTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void AlignedNonzeroRawOffsetsRemainSupported(int sector)
    {
        Guid part = Guid.NewGuid();
        var plan = new DiskImageCoveragePlan((ulong)sector * 2, (uint)sector,
            [new(part, (ulong)sector * 2)],
            [new(0, (ulong)sector, DiskImageExtentKind.Stored, part, (ulong)sector),
             new((ulong)sector, (ulong)sector, DiskImageExtentKind.Zero)], true);
        Assert.Equal(DiskImageCoverageState.CompleteUnverified, DiskImageCoverageService.Assess(plan).State);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void RawAndExplicitZeroCoverageIsCompleteButNeverVerified(int sector)
    {
        Guid part = Guid.NewGuid();
        var plan = new DiskImageCoveragePlan((ulong)sector * 2, (uint)sector,
            [new(part, (ulong)sector)],
            [new(0, (ulong)sector, DiskImageExtentKind.Stored, part), new((ulong)sector, (ulong)sector, DiskImageExtentKind.Zero)], true);
        DiskImageCoverageAssessment result = DiskImageCoverageService.Assess(plan);
        Assert.Equal(DiskImageCoverageState.CompleteUnverified, result.State);
        Assert.Equal(plan.Capacity, result.CoveredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCompletionOrTailCoverageCannotAppearComplete(bool missingTail)
    {
        var plan = new DiskImageCoveragePlan(missingTail ? 1024UL : 512UL, 512, [],
            [new(0, 512, DiskImageExtentKind.Zero)], missingTail);
        Assert.Equal(DiskImageCoverageState.Incomplete, DiskImageCoverageService.Assess(plan).State);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("gap")]
    [InlineData("misaligned")]
    [InlineData("zero-length")]
    [InlineData("past-end")]
    [InlineData("overflow")]
    public void ImpossibleLogicalMapsAreRejected(string defect)
    {
        DiskImageExtent[] extents = defect switch
        {
            "overlap" => [new(0, 512, DiskImageExtentKind.Zero), new(0, 512, DiskImageExtentKind.Zero)],
            "gap" => [new(512, 512, DiskImageExtentKind.Zero)],
            "misaligned" => [new(0, 513, DiskImageExtentKind.Zero)],
            "zero-length" => [new(0, 0, DiskImageExtentKind.Zero)],
            "past-end" => [new(0, 1536, DiskImageExtentKind.Zero)],
            _ => [new(ulong.MaxValue, 512, DiskImageExtentKind.Zero)]
        };
        var plan = new DiskImageCoveragePlan(1024, 512, [], extents, true);
        Assert.Equal(DiskImageCoverageState.Invalid, DiskImageCoverageService.Assess(plan).State);
    }

    [Theory]
    [InlineData("missing-part")]
    [InlineData("part-offset")]
    [InlineData("part-tail")]
    [InlineData("duplicate-part")]
    [InlineData("zero-with-part")]
    [InlineData("zero-with-offset")]
    public void PartIdentityBoundsAndExplicitZeroRulesAreEnforced(string defect)
    {
        Guid part = Guid.NewGuid();
        DiskImagePayloadPart[] parts = [new(part, 512)];
        DiskImageExtent extent = defect switch
        {
            "missing-part" => new(0, 512, DiskImageExtentKind.Stored, Guid.NewGuid()),
            "part-offset" => new(0, 512, DiskImageExtentKind.Stored, part, ulong.MaxValue),
            "part-tail" => new(0, 512, DiskImageExtentKind.Stored, part, 1),
            "zero-with-part" => new(0, 512, DiskImageExtentKind.Zero, part),
            "zero-with-offset" => new(0, 512, DiskImageExtentKind.Zero, null, 1),
            _ => new(0, 512, DiskImageExtentKind.Stored, part)
        };
        if (defect == "duplicate-part")
            parts = [parts[0], parts[0]];
        Assert.Equal(DiskImageCoverageState.Invalid,
            DiskImageCoverageService.Assess(new(512, 512, parts, [extent], true)).State);
    }

    [Fact]
    public void HugeLogicalCapacityDoesNotRequirePayloadAllocationOrOverflow()
    {
        ulong capacity = ulong.MaxValue - 511;
        var plan = new DiskImageCoveragePlan(capacity, 512, [], [new(0, capacity, DiskImageExtentKind.Zero)], true);
        Assert.Equal(capacity, DiskImageCoverageService.Assess(plan).CoveredBytes);
        Assert.Equal(DiskImageCoverageState.CompleteUnverified, DiskImageCoverageService.Assess(plan).State);
    }

    [Fact]
    public void CountBudgetsRejectInputBeforeProcessingItsEntries()
    {
        var plan = new DiskImageCoveragePlan(512, 512, [], [new(0, 512, DiskImageExtentKind.Zero)], true);
        Assert.Equal("image.map-limit", DiskImageCoverageService.Assess(plan with
        { Parts = Enumerable.Repeat(new DiskImagePayloadPart(Guid.Empty, 0), 4097).ToArray() }).Reason);
        Assert.Equal("image.map-limit", DiskImageCoverageService.Assess(plan with
        { Extents = Enumerable.Repeat(new DiskImageExtent(0, 0, DiskImageExtentKind.Zero), 65537).ToArray() }).Reason);
    }

    [Fact]
    public void UnknownGeometryAndExtentKindsAreExplicitlyUnsupported()
    {
        var plan = new DiskImageCoveragePlan(512, 512, [], [new(0, 512, DiskImageExtentKind.Zero)], true);
        Assert.Equal(DiskImageCoverageState.Unsupported, DiskImageCoverageService.Assess(plan with { LogicalSectorBytes = 0 }).State);
        Assert.Equal(DiskImageCoverageState.Unsupported, DiskImageCoverageService.Assess(plan with
        { Extents = [new(0, 512, (DiskImageExtentKind)99)] }).State);
        Assert.Equal(DiskImageCoverageState.Invalid, DiskImageCoverageService.Assess(plan with { Capacity = 0 }).State);
    }
}
