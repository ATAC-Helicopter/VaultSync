using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliRestoreCommandTests
{
    [Fact]
    public async Task RestoreCopiesRecordedBackupInsteadOfCurrentProjectState()
    {
        using var root = new TempDirectory();
        string projectRoot = Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName;
        string backupRoot = Directory.CreateDirectory(Path.Combine(root.Path, "backups")).FullName;
        string backupRelative = Path.Combine("Project", "point-1");
        string backupFolder = Directory.CreateDirectory(Path.Combine(backupRoot, backupRelative)).FullName;
        string destination = Path.Combine(root.Path, "restore");
        string database = Path.Combine(root.Path, "vaultsync.db");
        File.WriteAllText(Path.Combine(projectRoot, "state.txt"), "current-live-state");
        File.WriteAllText(Path.Combine(backupFolder, "state.txt"), "recorded-backup-state");

        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "CLI Restore", projectRoot);
        int snapshotId = repository.CreateSnapshot(projectId, 1, 21);
        repository.InsertFiles(snapshotId,
        [
            new FileEntry("state.txt", 21, DateTime.UtcNow, string.Empty)
        ]);
        repository.CreateBackup(
            projectId,
            snapshotId,
            "manual",
            21,
            backupRelative,
            backupRoot,
            "Test");

        int exitCode = await Program.Main(
            ["restore", "CLI Restore", destination, "--db", database, "--quiet"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("recorded-backup-state", File.ReadAllText(Path.Combine(destination, "state.txt")));
    }
}
