using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.CLI.Commands;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliRestoreCommandTests
{
    public CliRestoreCommandTests() => _ = Spectre.Console.AnsiConsole.Console;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreCopiesRecordedBackupInsteadOfCurrentProjectState(bool groupedRoute)
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

        string[] args = ["restore", "CLI Restore", destination, "--db", database, "--quiet"];
        int exitCode = await Program.Main(groupedRoute ? ["recovery", .. args] : args);

        Assert.Equal(0, exitCode);
        Assert.Equal("recorded-backup-state", File.ReadAllText(Path.Combine(destination, "state.txt")));
    }

    [Fact]
    public async Task RestoreRejectsTamperedBackupBeforeWritingDestination()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "tampered.txt");
        File.WriteAllText(source, "tampered");
        string expectedHash = Convert.ToHexString(SHA256.HashData("expected"u8.ToArray()));
        var expected = new FileEntry("tampered.txt", new FileInfo(source).Length, DateTime.UtcNow, expectedHash);

        await Assert.ThrowsAsync<InvalidDataException>(() => RestoreCommand.VerifyBackupFileAsync(
            source,
            expected,
            new HashService(),
            default));
    }

    [Fact]
    public async Task StagedRestoreMismatchLeavesExistingTargetAndNoTemporaryFile()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "recorded.txt");
        string target = Path.Combine(root.Path, "target.txt");
        File.WriteAllText(source, "changed after planning");
        File.WriteAllText(target, "keep this");
        var expected = new FileEntry("recorded.txt", 8, DateTime.UtcNow,
            Convert.ToHexString(SHA256.HashData("recorded"u8.ToArray())));

        await Assert.ThrowsAsync<InvalidDataException>(() => RestoreCommand.CopyVerifiedFileAtomicallyAsync(
            source, target, expected, default));

        Assert.Equal("keep this", File.ReadAllText(target));
        Assert.DoesNotContain(Directory.GetFiles(root.Path), path => path.EndsWith(".vaultsync-restore.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreCleanRemovesExtraFilesAndEmptyDirectories()
    {
        using var fixture = RestoreFixture.Create();
        string staleDirectory = Directory.CreateDirectory(
            Path.Combine(fixture.Destination, "stale")).FullName;
        string staleFile = Path.Combine(staleDirectory, "old.txt");
        File.WriteAllText(staleFile, "obsolete");

        int exitCode = await Program.Main(
            ["restore", fixture.ProjectName, fixture.Destination, "--db", fixture.Database, "--clean"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("recorded", File.ReadAllText(Path.Combine(fixture.Destination, "state.txt")));
        Assert.False(File.Exists(staleFile));
        Assert.False(Directory.Exists(staleDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DryRunReportsCleanPlanWithoutChangingDestination(bool groupedRoute)
    {
        using var fixture = RestoreFixture.Create();
        Directory.CreateDirectory(fixture.Destination);
        string staleFile = Path.Combine(fixture.Destination, "old.txt");
        File.WriteAllText(staleFile, "keep-me");

        string[] args = ["restore", fixture.ProjectName, fixture.Destination, "--db", fixture.Database, "--clean", "--dry-run"];
        int exitCode = await Program.Main(groupedRoute ? ["recovery", .. args] : args);

        Assert.Equal(0, exitCode);
        Assert.Equal("keep-me", File.ReadAllText(staleFile));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "state.txt")));
    }

    [Fact]
    public async Task JsonRestoreReportsSelectedSnapshotAndOptions()
    {
        using var fixture = RestoreFixture.Create();
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            int exitCode = await Program.Main(
            [
                "restore", fixture.ProjectName, fixture.Destination,
                "--db", fixture.Database,
                "--snapshot", fixture.SnapshotId.ToString(),
                "--json", "--keep-empty-dirs"
            ]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        string json = output.ToString();
        Assert.Contains($"\"snapshotId\": {fixture.SnapshotId}", json, StringComparison.Ordinal);
        Assert.Contains("\"keepEmptyDirs\": true", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectiveRestoreUsesBackupIdAndPreservesUnselectedTargetFiles()
    {
        using var fixture = RestoreFixture.Create();
        Directory.CreateDirectory(fixture.Destination);
        string untouched = Path.Combine(fixture.Destination, "untouched.txt");
        File.WriteAllText(untouched, "keep");
        TextWriter previousOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            int code = await Program.Main(
            [
                "recovery", "restore", fixture.ProjectName, fixture.Destination,
                "--db", fixture.Database, "--backup-id", fixture.BackupId.ToString(),
                "--include", "notes", "--output", "json"
            ]);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetOut(previousOutput);
        }

        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("recovery.restore", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("selectedFiles").GetInt32());
        Assert.Equal("recorded notes", File.ReadAllText(Path.Combine(fixture.Destination, "notes", "readme.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "state.txt")));
        Assert.Equal("keep", File.ReadAllText(untouched));
    }

    [Fact]
    public async Task SelectiveRestoreRejectsCleanBeforeChangingTarget()
    {
        using var fixture = RestoreFixture.Create();
        string untouched = Path.Combine(fixture.Destination, "untouched.txt");
        Directory.CreateDirectory(fixture.Destination);
        File.WriteAllText(untouched, "keep");
        TextWriter previousOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            int code = await Program.Main(
            [
                "recovery", "restore", fixture.ProjectName, fixture.Destination,
                "--db", fixture.Database, "--include", "notes", "--clean", "--output", "json"
            ]);
            Assert.Equal(2, code);
        }
        finally
        {
            Console.SetOut(previousOutput);
        }
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid_options", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("keep", File.ReadAllText(untouched));
    }

    private sealed class RestoreFixture : IDisposable
    {
        private readonly TempDirectory _root = new();

        private RestoreFixture()
        {
            string projectRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "project")).FullName;
            string backupRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "backups")).FullName;
            string relative = Path.Combine("Project", "point-1");
            string backupFolder = Directory.CreateDirectory(Path.Combine(backupRoot, relative)).FullName;
            File.WriteAllText(Path.Combine(backupFolder, "state.txt"), "recorded");
            string notes = Directory.CreateDirectory(Path.Combine(backupFolder, "notes")).FullName;
            File.WriteAllText(Path.Combine(notes, "readme.txt"), "recorded notes");

            ProjectName = "CLI Restore Coverage";
            Destination = Path.Combine(_root.Path, "restore");
            Database = Path.Combine(_root.Path, "vaultsync.db");
            SqliteRepository repository = TestRepository.Create(Database);
            int projectId = TestRepository.AddProject(repository, ProjectName, projectRoot);
            SnapshotId = repository.CreateSnapshot(projectId, 2, 22);
            repository.InsertFiles(SnapshotId,
            [
                new FileEntry("state.txt", 8, DateTime.UtcNow, string.Empty),
                new FileEntry("notes/readme.txt", 14, DateTime.UtcNow, string.Empty)
            ]);
            BackupId = repository.CreateBackup(projectId, SnapshotId, "manual", 22, relative, backupRoot, "Test");
        }

        public string ProjectName { get; }
        public string Destination { get; }
        public string Database { get; }
        public int SnapshotId { get; }
        public int BackupId { get; }

        public static RestoreFixture Create() => new();

        public void Dispose() => _root.Dispose();
    }
}
