using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

/// <summary>
/// Focused, disposable-fixture matrix for public reliability regression #846.
/// Verifies the service entry guards; does not qualify symlink/race or mounted-device security.
/// </summary>
public sealed class BackupTopologyMatrix846Tests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public static IEnumerable<object[]> UnsafePreflightCases()
    {
        foreach (string scenario in UnsafeScenarios)
            foreach (bool archive in new[] { false, true })
                yield return new object[] { scenario, archive };
    }

    public static IEnumerable<object[]> UnsafeExecutionCases()
    {
        foreach (string scenario in UnsafeScenarios)
            foreach (bool archive in new[] { false, true })
                foreach (bool automatic in new[] { false, true })
                    yield return new object[] { scenario, archive, automatic };
    }

    public static IEnumerable<object[]> UnsafeFinalRootCases()
    {
        foreach (string scenario in UnsafeScenarios)
            foreach (bool archive in new[] { false, true })
                yield return new object[] { scenario, archive };
    }

    public static IEnumerable<object[]> SafePreflightCases()
    {
        foreach (string scenario in SafeScenarios)
            foreach (bool archive in new[] { false, true })
                yield return new object[] { scenario, archive };
    }

    public static IEnumerable<object[]> SafeExecutionCases()
    {
        foreach (string scenario in SafeScenarios)
            foreach (bool archive in new[] { false, true })
                foreach (bool automatic in new[] { false, true })
                    yield return new object[] { scenario, archive, automatic };
    }

    public static IEnumerable<object[]> InvalidServiceInputs()
    {
        foreach (bool archive in new[] { false, true })
        {
            yield return new object[] { "empty-source", archive };
            yield return new object[] { "empty-destination", archive };
            yield return new object[] { "missing-destination", archive };
        }
    }

    // Distinct physical / lexical topologies; all remain within a unique temp workspace.
    private static readonly string[] UnsafeScenarios =
    [
        "nested",
        "nested-multiple-levels",
        "equal",
        "source-in-destination",
        "source-deep-in-destination",
        "nested-trailing-separator",
        "equal-trailing-separator",
        "nested-unicode",
        "nested-spaces",
        "nested-dot",
        "nested-parent-segment",
        "nested-custom-folder",
        "nested-missing-destination",
        "ancestor-trailing-separator",
        "same-via-parent-segment"
    ];

    private static readonly string[] SafeScenarios =
    [
        "sibling",
        "sibling-prefix-collision",
        "source-prefix-collision",
        "separate-deep-roots",
        "unicode-siblings",
        "spaces-and-dots"
    ];

    [Theory]
    [MemberData(nameof(UnsafePreflightCases))]
    public async Task Preflight_RejectsUnsafeTopologyBeforeScanningOrCache(string scenario, bool archive)
    {
        Fixture f = SetupUnsafe(scenario);
        var service = new BackupService(f.Repository);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(f.Project, f.BackupRoot, useArchiveMode: archive,
                cacheTtl: TimeSpan.FromHours(1)));
        Assert.Contains("Unsafe backup destination", error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoArtifacts(f);
    }

    [Theory]
    [MemberData(nameof(UnsafeExecutionCases))]
    public async Task RunBackup_RejectsUnsafeTopologyBeforeSideEffects(
        string scenario, bool archive, bool automatic)
    {
        Fixture f = SetupUnsafe(scenario);
        var service = new BackupService(f.Repository);
        int callbackCount = 0;
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.BackupRoot,
                isAuto: automatic, useArchiveMode: archive,
                progressCallback: (_, _, _) => callbackCount++));
        Assert.Contains("Unsafe backup destination", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, callbackCount);
        AssertNoArtifacts(f);
    }

    [Theory]
    [MemberData(nameof(UnsafeFinalRootCases))]
    public async Task RunBackup_RejectsUnsafePreferredFinalRootBeforeStaging(string scenario, bool archive)
    {
        Fixture f = SetupUnsafe(scenario);
        // An independent, safe scratch destination must not be touched if final root is unsafe.
        string safeStage = Path.Combine(f.Workspace, "safe-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(safeStage);
        var service = new BackupService(f.Repository);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(new BackupService.BackupRunRequest
            {
                Project = f.Project,
                BackupRoot = safeStage,
                PreferredFinalBackupRoot = f.BackupRoot,
                UseArchiveMode = archive,
                IsAuto = true
            }));
        Assert.Contains("Unsafe backup destination", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(safeStage));
        AssertNoArtifacts(f);
    }

    [Theory]
    [MemberData(nameof(SafePreflightCases))]
    public async Task Preflight_AcceptsClearlySeparatedRoots(string scenario, bool archive)
    {
        Fixture f = SetupSafe(scenario);
        var service = new BackupService(f.Repository);
        BackupService.BackupPreflightResult result = await service.PreflightBackupAsync(
            f.Project, f.BackupRoot, useArchiveMode: archive, cacheTtl: TimeSpan.Zero);
        Assert.Equal(1, result.TotalFiles);
        Assert.True(result.TotalBytes > 0);
        Assert.False(result.UsedCache);
        AssertNoArtifacts(f);
    }

    [Theory]
    [MemberData(nameof(SafeExecutionCases))]
    public async Task RunBackup_PreservesExternalDestinationsAndContents(
        string scenario, bool archive, bool automatic)
    {
        Fixture f = SetupSafe(scenario);
        var service = new BackupService(f.Repository);
        BackupService.BackupRunResult result = await service.RunBackupAsync(
            f.Project, f.BackupRoot, isAuto: automatic, useArchiveMode: archive);
        Assert.True(result.BackupId > 0);
        Assert.False(result.Cancelled);
        Backup saved = f.Repository.GetBackupById(result.BackupId)!;
        string folder = Path.Combine(f.BackupRoot, saved.Path);
        Assert.True(File.Exists(Path.Combine(folder, ".vaultsync_complete")));
        if (archive)
        {
            using ZipArchive zip = ZipFile.OpenRead(Path.Combine(
                folder, BackupArchiveCryptoService.PlainArchiveFileName));
            Assert.Equal(["important.txt"], zip.Entries.Select(x => x.FullName).ToArray());
            using var reader = new StreamReader(zip.GetEntry("important.txt")!.Open());
            Assert.Equal("source contents", await reader.ReadToEndAsync());
        }
        else
        {
            Assert.Equal("source contents", await File.ReadAllTextAsync(
                Path.Combine(folder, "important.txt")));
        }
        Assert.Single(f.Repository.GetBackupsForProject(f.Project.Id));
    }

    [Theory]
    [MemberData(nameof(InvalidServiceInputs))]
    public async Task Inputs_RejectedBeforeAnyArchiveOrMetadata(string scenario, bool archive)
    {
        Fixture f = SetupSafe("sibling");
        Project project = scenario == "empty-source"
            ? f.Project with
            {
                RootPath = string.Empty
            }
            : f.Project;
        string backup = scenario switch
        {
            "empty-destination" => " ",
            "missing-destination" => Path.Combine(f.Workspace, "missing-root"),
            _ => f.BackupRoot
        };
        var service = new BackupService(f.Repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(project, backup, useArchiveMode: archive));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(project, backup, isAuto: false, useArchiveMode: archive));
        Assert.Empty(f.Repository.GetBackupsForProject(project.Id));
        Assert.Empty(f.Repository.GetSnapshotsForProject(project.Name));
    }

    [Fact]
    public async Task RejectedRun_DoesNotAlterPreviouslyCompletedArchiveOrHistory()
    {
        Fixture f = SetupSafe("sibling");
        var service = new BackupService(f.Repository);
        BackupService.BackupRunResult first = await service.RunBackupAsync(
            f.Project, f.BackupRoot, isAuto: false, useArchiveMode: true);
        Backup initial = f.Repository.GetBackupById(first.BackupId)!;
        string existingArchive = Path.Combine(
            f.BackupRoot, initial.Path, BackupArchiveCryptoService.PlainArchiveFileName);
        byte[] originalArchive = await File.ReadAllBytesAsync(existingArchive);
        int priorSnapshots = f.Repository.GetSnapshotsForProject(f.Project.Name).Count();

        // Retain the same project identity but move its source to the previously safe
        // destination: an old backup must not make the new unsafe configuration pass.
        Project unsafeVersion = f.Project with
        {
            RootPath = f.BackupRoot
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(unsafeVersion, f.BackupRoot,
                useArchiveMode: true, cacheTtl: TimeSpan.FromHours(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(unsafeVersion, f.BackupRoot,
                isAuto: true, useArchiveMode: true));

        Assert.Single(f.Repository.GetBackupsForProject(f.Project.Id));
        Assert.Equal(priorSnapshots, f.Repository.GetSnapshotsForProject(f.Project.Name).Count());
        Assert.Equal(originalArchive, await File.ReadAllBytesAsync(existingArchive));
    }

    [Fact]
    public async Task SiblingNonArchiveDestination_ThreeIndependentRunsRemainUsable()
    {
        Fixture f = SetupSafe("sibling");
        var service = new BackupService(f.Repository);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            BackupService.BackupRunResult result = await service.RunBackupAsync(
                f.Project, f.BackupRoot, isAuto: attempt % 2 == 0, useArchiveMode: false);
            Assert.True(result.BackupId > 0);
            Backup backup = f.Repository.GetBackupById(result.BackupId)!;
            string payload = Path.Combine(f.BackupRoot, backup.Path, "important.txt");
            Assert.Equal("source contents", await File.ReadAllTextAsync(payload));
        }
        Assert.Equal(3, f.Repository.GetBackupsForProject(f.Project.Id).Count());
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("nested-multiple-levels")]
    [InlineData("source-deep-in-destination")]
    public async Task PreflightAndRuntime_ExplainSameUnsafeTopology(string scenario)
    {
        Fixture f = SetupUnsafe(scenario);
        var service = new BackupService(f.Repository);
        InvalidOperationException preflight = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(f.Project, f.BackupRoot, useArchiveMode: true));
        InvalidOperationException runtime = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.BackupRoot, isAuto: true, useArchiveMode: true));
        Assert.Equal(preflight.Message, runtime.Message);
        AssertNoArtifacts(f);
    }

    private Fixture SetupUnsafe(string scenario)
    {
        string workspace = NewWorkspace();
        string source = Path.Combine(workspace, "source");
        string backup = Path.Combine(source, "backups");
        bool createBackup = true;
        switch (scenario)
        {
            case "nested":
                break;
            case "nested-multiple-levels":
                backup = Path.Combine(source, "level-1", "level-2", "backups");
                break;
            case "equal":
                backup = source;
                break;
            case "source-in-destination":
                backup = Path.Combine(workspace, "backups");
                source = Path.Combine(backup, "source");
                break;
            case "source-deep-in-destination":
                backup = Path.Combine(workspace, "backups");
                source = Path.Combine(backup, "level-1", "level-2", "source");
                break;
            case "nested-trailing-separator":
                backup += Path.DirectorySeparatorChar;
                break;
            case "equal-trailing-separator":
                backup = source + Path.DirectorySeparatorChar;
                break;
            case "nested-unicode":
                backup = Path.Combine(source, "نسخ-احتياطية-äö", "nested");
                break;
            case "nested-spaces":
                backup = Path.Combine(source, "backup folder", "nested backup");
                break;
            case "nested-dot":
                backup = Path.Combine(source, ".", "backups");
                break;
            case "nested-parent-segment":
                Directory.CreateDirectory(Path.Combine(source, "placeholder"));
                backup = Path.Combine(source, "placeholder", "..", "backups");
                break;
            case "nested-custom-folder":
                backup = Path.Combine(source, "non-reserved-custom-data");
                break;
            case "nested-missing-destination":
                createBackup = false;
                break;
            case "ancestor-trailing-separator":
                backup = workspace + Path.DirectorySeparatorChar;
                break;
            case "same-via-parent-segment":
                Directory.CreateDirectory(Path.Combine(source, "placeholder"));
                backup = Path.Combine(source, "placeholder", "..");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return CreateFixture(workspace, source, backup, createBackup);
    }

    private Fixture SetupSafe(string scenario)
    {
        string workspace = NewWorkspace();
        string source = Path.Combine(workspace, "source");
        string backup = Path.Combine(workspace, "backups");
        switch (scenario)
        {
            case "sibling":
                break;
            case "sibling-prefix-collision":
                backup = Path.Combine(workspace, "source-backups");
                break;
            case "source-prefix-collision":
                source = Path.Combine(workspace, "source-archive");
                backup = Path.Combine(workspace, "source");
                break;
            case "separate-deep-roots":
                source = Path.Combine(workspace, "projects", "2026", "source");
                backup = Path.Combine(workspace, "storage", "2026", "backups");
                break;
            case "unicode-siblings":
                source = Path.Combine(workspace, "المصدر-äö");
                backup = Path.Combine(workspace, "النسخ-äö");
                break;
            case "spaces-and-dots":
                source = Path.Combine(workspace, "source with spaces");
                backup = Path.Combine(workspace, "backups with spaces", ".", "child");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return CreateFixture(workspace, source, backup, createBackup: true);
    }

    private string NewWorkspace()
    {
        string workspace = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static Fixture CreateFixture(
        string workspace, string source, string backup, bool createBackup)
    {
        Directory.CreateDirectory(source);
        if (createBackup)
            Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(source, "important.txt"), "source contents", Encoding.UTF8);
        SqliteRepository repo = TestRepository.Create(Path.Combine(workspace, "test.db"));
        int id = TestRepository.AddProject(repo, "Topology Matrix", source, preset: string.Empty);
        return new Fixture(workspace, repo, repo.GetProjectById(id)!, backup);
    }

    private static void AssertNoArtifacts(Fixture f)
    {
        Assert.Equal("source contents", File.ReadAllText(
            Path.Combine(f.Project.RootPath, "important.txt")));
        Assert.False(Directory.Exists(Path.Combine(
            f.BackupRoot, BackupService.GetProjectBackupFolderName(f.Project.Name))));
        Assert.Empty(f.Repository.GetBackupsForProject(f.Project.Id));
        Assert.Empty(f.Repository.GetSnapshotsForProject(f.Project.Name));
    }

    private sealed record Fixture(
        string Workspace, SqliteRepository Repository, Project Project, string BackupRoot);

    public void Dispose() => _temp.Dispose();
}
