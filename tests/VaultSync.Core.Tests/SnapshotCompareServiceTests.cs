using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotCompareServiceTests
{
    [Fact]
    public async Task CompareAsync_RejectsTheSameSnapshotOnBothSides()
    {
        using var temp = new TempDirectory();
        SqliteRepository repository = TestRepository.Create(Path.Combine(temp.Path, "vaultsync.db"));
        var service = new SnapshotCompareService(repository);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CompareAsync(1, 1));
    }

    [Fact]
    public async Task CompareAsync_LoadsArbitrarySnapshotsFromRepository()
    {
        using var temp = new TempDirectory();
        SqliteRepository repository = TestRepository.Create(Path.Combine(temp.Path, "vaultsync.db"));
        int projectId = TestRepository.AddProject(repository, "Compare Project", temp.Path);
        int olderId = repository.CreateSnapshot(projectId, 1, 10);
        int newerId = repository.CreateSnapshot(projectId, 2, 25);
        DateTime time = DateTime.UtcNow;
        repository.InsertFiles(olderId, [new FileEntry("src/app.cs", 10, time, "old")]);
        repository.InsertFiles(newerId,
        [
            new FileEntry("src/app.cs", 15, time, "new"),
            new FileEntry("src/new.cs", 10, time, "added")
        ]);

        var service = new SnapshotCompareService(repository);
        SnapshotCompareResult result = await service.CompareAsync(olderId, newerId, CancellationToken.None);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Modified);
        Assert.Equal(15, result.NetSizeBytes);
    }

    [Fact]
    public void Compare_ClassifiesFileLevelChangesAndTopPaths()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older =
        [
            new("src/keep.cs", 10, time, "same"),
            new("src/change.cs", 20, time, "old"),
            new("docs/remove.md", 30, time, "remove")
        ];
        FileEntry[] newer =
        [
            new("src/keep.cs", 10, time.AddMinutes(1), "same"),
            new("src/change.cs", 25, time, "new"),
            new("src/add.cs", 15, time, "add")
        ];

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Modified);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(-10, result.NetSizeBytes);
        Assert.Equal(3, result.Changes.Count);
        Assert.Equal("src", result.TopChangedPaths[0].Path);
        Assert.Equal(2, result.TopChangedPaths[0].Changes);
        Assert.Contains(result.Changes, change => change.Path == "docs/remove.md" && change.Kind == SnapshotFileChangeKind.Deleted);
    }

    [Fact]
    public void Compare_DetectsLargeDeletionGrowthAndChurnSignals()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older = Enumerable.Range(1, 4)
            .Select(index => new FileEntry($"old/{index}.txt", 10, time, $"old-{index}"))
            .ToArray();
        FileEntry[] newer =
        [
            new("old/1.txt", 10, time, "old-1"),
            new("new/large.bin", 100, time, "large")
        ];
        var options = new SnapshotCompareOptions(
            MassDeletionMinimumFiles: 2,
            MassDeletionRatio: 0.50,
            SignificantGrowthMinimumBytes: 50,
            SignificantGrowthRatio: 0.50,
            HighChurnMinimumFiles: 3,
            HighChurnRatio: 0.50);

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer, options);

        Assert.Contains(result.Signals, signal => signal.Kind == SnapshotChangeSignalKind.MassDeletion);
        Assert.Contains(result.Signals, signal => signal.Kind == SnapshotChangeSignalKind.SignificantGrowth);
        Assert.Contains(result.Signals, signal => signal.Kind == SnapshotChangeSignalKind.HighChurn);
    }

    [Fact]
    public void Compare_NormalizesPathSeparatorsAndUsesMetadataWhenHashesAreMissing()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older = [new("src\\app.cs", 10, time, string.Empty)];
        FileEntry[] newer = [new("src/app.cs", 10, time.AddMilliseconds(500), string.Empty)];

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer);

        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(1, result.Unchanged);
    }

    [Fact]
    public void Compare_TreatsDifferentSizesAsModifiedEvenWhenHashesMatch()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older = [new("file.txt", 10, time, "same-hash")];
        FileEntry[] newer = [new("file.txt", 11, time, "same-hash")];

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer);

        Assert.Equal(1, result.Modified);
    }

    [Fact]
    public void Compare_PreservesCaseDistinctPaths()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older = [new("src/Foo.txt", 10, time, "upper")];
        FileEntry[] newer = [new("src/foo.txt", 10, time, "lower")];

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Deleted);
        Assert.Contains(result.Changes, change => change.Path == "src/Foo.txt" && change.Kind == SnapshotFileChangeKind.Deleted);
        Assert.Contains(result.Changes, change => change.Path == "src/foo.txt" && change.Kind == SnapshotFileChangeKind.Added);
    }

    [Fact]
    public void Compare_HandlesLargeInventoriesWithoutDroppingChanges()
    {
        DateTime time = DateTime.UtcNow;
        FileEntry[] older = Enumerable.Range(0, 10_000)
            .Select(index => new FileEntry($"src/{index:D5}.txt", 10, time, $"old-{index}"))
            .ToArray();
        FileEntry[] newer = Enumerable.Range(0, 10_000)
            .Select(index => new FileEntry(
                $"src/{index:D5}.txt",
                index % 10 == 0 ? 11 : 10,
                time,
                index % 10 == 0 ? $"new-{index}" : $"old-{index}"))
            .ToArray();

        SnapshotCompareResult result = SnapshotCompareService.Compare(older, newer);

        Assert.Equal(1_000, result.Modified);
        Assert.Equal(9_000, result.Unchanged);
        Assert.Equal(1_000, result.Changes.Count);
    }

    [Fact]
    public void Compare_EmptyInventoriesDoNotReportGrowth()
    {
        SnapshotCompareResult result = SnapshotCompareService.Compare([], []);

        Assert.Equal(0, result.NetSizeBytes);
        Assert.Empty(result.Signals);
    }
}
