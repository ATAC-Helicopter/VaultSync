using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.CLI.Commands;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PruneDateParsingTests
{
    [Fact]
    public void TryParseBeforeDateAcceptsExactInvariantDate()
    {
        bool parsed = PruneSettings.TryParseBeforeDate("2026-09-11", out DateTime value);

        Assert.True(parsed);
        Assert.Equal(new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [Theory]
    [InlineData("09/11/2026")]
    [InlineData("2026-02-30")]
    public void TryParseBeforeDateRejectsAmbiguousOrInvalidDates(string value)
    {
        Assert.False(PruneSettings.TryParseBeforeDate(value, out _));
    }

    [Fact]
    public void PlanPrunePreservesBackedOrProtectedSnapshots()
    {
        Snapshot[] snapshots =
        [
            new() { Id = 3, CreatedUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = 2, CreatedUtc = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = 1, CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) }
        ];
        var settings = new PruneSettings { KeepLast = 0 };

        List<int> planned = PruneCommand.PlanPrune(snapshots, settings, new HashSet<int> { 1, 2 });

        Assert.Equal([3], planned);
    }

    [Fact]
    public async Task PruneCommandPreservesBackedAndProtectedSnapshotsEndToEnd()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vaultsync.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "CLI Prune", root.Path);
        int backed = repository.CreateSnapshot(projectId, 0, 0);
        int protectedSnapshot = repository.CreateSnapshot(projectId, 0, 0);
        int eligible = repository.CreateSnapshot(projectId, 0, 0);
        repository.CreateBackup(projectId, backed, "manual", 0, "backup", root.Path, "Test");
        repository.UpsertSnapshotHistoryMetadata(new SnapshotHistoryMetadata
        {
            SnapshotId = protectedSnapshot,
            IsProtected = true
        });

        int exitCode = await Program.Main(
        [
            "prune", "CLI Prune", "--before", "2099-01-01",
            "--db", database, "--json"
        ]);

        Assert.Equal(0, exitCode);
        int[] remaining = [.. repository.GetSnapshotsForProject("CLI Prune").Select(snapshot => snapshot.Id)];
        Assert.Contains(backed, remaining);
        Assert.Contains(protectedSnapshot, remaining);
        Assert.DoesNotContain(eligible, remaining);
    }
}
