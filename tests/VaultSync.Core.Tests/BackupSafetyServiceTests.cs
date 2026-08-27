using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupSafetyServiceTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();

    [Fact]
    public async Task ScannerService_ScanAsyncRequiresARootPath()
    {
        var scanner = new ScannerService(new FilterService([]));

        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanAsync(string.Empty));
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksBackupRootInsideProjectRoot()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        var backupRoot = Path.Combine(projectRoot, ".vaultsync-temp-backups");
        Directory.CreateDirectory(projectRoot);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot));

        Assert.Contains("inside the project root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksProjectRootInsideBackupRoot()
    {
        var backupRoot = Path.Combine(_tempDir.Path, "backups");
        var projectRoot = Path.Combine(backupRoot, "project");
        Directory.CreateDirectory(projectRoot);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot));

        Assert.Contains("project root is inside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksSameDirectory()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        Directory.CreateDirectory(projectRoot);

        Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, projectRoot));
    }

    [Fact]
    public void EnsureSafeBackupRoot_AllowsSiblingBackupRoot()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        var backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(backupRoot);

        var ex = Record.Exception(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot));

        Assert.Null(ex);
    }

    [Fact]
    public void GetOfflineStagingRoot_IsOutsideProjectRoot()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var project = new ProjectBuilder()
            .WithId(42)
            .WithName("Project")
            .WithRootPath(projectRoot)
            .Build();

        var stagingRoot = BackupSafetyService.GetOfflineStagingRoot(project);

        BackupSafetyService.EnsureSafeBackupRoot(project, stagingRoot);
        Assert.True(Path.IsPathFullyQualified(stagingRoot));
        Assert.DoesNotContain(projectRoot, stagingRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCombinePathUnderRoot_AllowsNestedRelativePath()
    {
        var backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);

        bool combined = BackupSafetyService.TryCombinePathUnderRoot(
            backupRoot,
            "Project/2026-05-14_10-00-00",
            out string fullPath);

        Assert.True(combined);
        Assert.StartsWith(Path.GetFullPath(backupRoot), fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Project", "2026-05-14_10-00-00"), fullPath);
    }

    [Fact]
    public void TryCombinePathUnderRoot_PreservesFilesystemRoot()
    {
        string fullPath = Path.GetFullPath(_tempDir.Path);
        string root = Path.GetPathRoot(fullPath)!;
        string relative = Path.GetRelativePath(root, fullPath);

        bool combined = BackupSafetyService.TryCombinePathUnderRoot(root, relative, out string resolved);

        Assert.True(combined);
        Assert.Equal(fullPath, resolved);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Project/../../outside")]
    public void TryCombinePathUnderRoot_BlocksTraversal(string relativePath)
    {
        var backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);

        bool combined = BackupSafetyService.TryCombinePathUnderRoot(backupRoot, relativePath, out _);

        Assert.False(combined);
    }

    [Fact]
    public void TryCombinePathUnderRoot_BlocksAbsolutePath()
    {
        var backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);
        string absolutePath = Path.Combine(_tempDir.Path, "outside");

        bool combined = BackupSafetyService.TryCombinePathUnderRoot(backupRoot, absolutePath, out _);

        Assert.False(combined);
    }

    [Fact]
    public void TryResolveExistingFileUnderRoot_RejectsLinkedPathComponents()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "verify-root")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "verify-outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        bool resolved = BackupSafetyService.TryResolveExistingFileUnderRoot(
            root,
            "linked/secret.txt",
            out _);

        Assert.False(resolved);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    public void ResolveSnapshotSourceFile_RejectsPathsOutsideProject(string relativePath)
    {
        string root = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "snapshot-source")).FullName;

        Assert.Throws<InvalidDataException>(() =>
            BackupService.ResolveSnapshotSourceFile(root, relativePath));
    }

    [Fact]
    public void ResolveSnapshotSourceFile_RejectsLinkedSource()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "snapshot-linked-source")).FullName;
        string outside = Path.Combine(_tempDir.Path, "outside-source.txt");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), outside);

        Assert.Throws<InvalidDataException>(() =>
            BackupService.ResolveSnapshotSourceFile(root, "linked.txt"));
    }

    [Fact]
    public void FilterService_AlwaysExcludesVaultSyncBackupArtifacts()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        var tempBackupDir = Path.Combine(projectRoot, ".vaultsync-temp-backups", "Project", "2026-05-11_10-00-00");
        var backupsDir = Path.Combine(projectRoot, "Backups", "Project", "2026-05-11_09-00-00");
        Directory.CreateDirectory(tempBackupDir);
        Directory.CreateDirectory(backupsDir);
        File.WriteAllText(Path.Combine(projectRoot, "normal.txt"), "keep");
        File.WriteAllText(Path.Combine(tempBackupDir, "runaway.bin"), "exclude");
        File.WriteAllText(Path.Combine(backupsDir, "nested.bin"), "exclude");

        var scanner = new ScannerService(new FilterService(Array.Empty<string>()));
        var entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("normal.txt", entries);
        Assert.DoesNotContain(entries, path => path.Contains("runaway", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, path => path.Contains("nested", StringComparison.OrdinalIgnoreCase));
        Assert.Single(entries);
    }

    [Fact]
    public void ScannerService_SkipsLinkedFilesAndDirectories()
    {
        if (OperatingSystem.IsWindows())
            return;

        string projectRoot = Path.Combine(_tempDir.Path, "linked-project");
        string outsideRoot = Path.Combine(_tempDir.Path, "outside-project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(projectRoot, "inside.txt"), "inside");
        string outsideFile = Path.Combine(outsideRoot, "outside.txt");
        File.WriteAllText(outsideFile, "outside");
        Directory.CreateSymbolicLink(Path.Combine(projectRoot, "linked-directory"), outsideRoot);
        File.CreateSymbolicLink(Path.Combine(projectRoot, "linked-file.txt"), outsideFile);

        var scanner = new ScannerService(new FilterService(Array.Empty<string>()));
        string[] entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Equal(["inside.txt"], entries);
    }

    [Fact]
    public async Task SnapshotService_SkipsLinkedFilesAndDirectories()
    {
        if (OperatingSystem.IsWindows())
            return;

        string projectRoot = Path.Combine(_tempDir.Path, "snapshot-linked-project");
        string outsideRoot = Path.Combine(_tempDir.Path, "snapshot-outside-project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(projectRoot, "inside.txt"), "inside");
        string outsideFile = Path.Combine(outsideRoot, "outside.txt");
        File.WriteAllText(outsideFile, "outside");
        Directory.CreateSymbolicLink(Path.Combine(projectRoot, "linked-directory"), outsideRoot);
        File.CreateSymbolicLink(Path.Combine(projectRoot, "linked-file.txt"), outsideFile);

        string dbPath = Path.Combine(_tempDir.Path, "snapshot-linked-project.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        int projectId = TestRepository.AddProject(
            repo,
            "Linked Snapshot Project",
            projectRoot,
            preset: string.Empty);
        Project project = repo.GetProjectById(projectId)!;
        string scanCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync",
            "cache",
            "scan",
            $"{project.Id}.json");
        DeleteFileIfExists(scanCachePath);

        try
        {
            int snapshotId = await new SnapshotService(repo, new HashService()).CreateSnapshotAsync(
                project,
                fullHash: false,
                hashNow: false);

            Assert.Equal(
                ["inside.txt"],
                repo.GetFilesForSnapshot(snapshotId).Select(entry => entry.RelPath).ToArray());
        }
        finally
        {
            DeleteFileIfExists(scanCachePath);
        }
    }

    [Fact]
    public void ScanCacheStore_RejectsPreviousLinkedTraversalPolicy()
    {
        var project = new Project
        {
            Id = Guid.NewGuid().GetHashCode() & int.MaxValue,
            Name = "Previous Scan Policy",
            RootPath = Path.Combine(_tempDir.Path, "previous-scan-policy"),
            Preset = string.Empty
        };
        const string filterHash = "policy-test";
        string scanCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync",
            "cache",
            "scan",
            $"{project.Id}.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scanCachePath)!);
            File.WriteAllText(
                scanCachePath,
                JsonSerializer.Serialize(new ScanCacheState
                {
                    Version = 1,
                    RootPath = project.RootPath,
                    FilterHash = filterHash,
                    DirectoryMtimeUtcTicks = new Dictionary<string, long>
                    {
                        ["linked-directory"] = DateTime.UtcNow.Ticks
                    }
                }));

            Assert.Null(ScanCacheStore.TryLoad(project, filterHash));
        }
        finally
        {
            DeleteFileIfExists(scanCachePath);
        }
    }

    [Fact]
    public void FilterService_DoubleStarDirectoryPatternExcludesNestedBuildOutput()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "project");
        var nestedBin = Path.Combine(projectRoot, "src", "App", "bin", "Debug");
        Directory.CreateDirectory(nestedBin);
        File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "keep");
        File.WriteAllText(Path.Combine(nestedBin, "App.dll"), "exclude");

        var scanner = new ScannerService(new FilterService(["**/bin/**"]));
        var entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("Program.cs", entries);
        Assert.DoesNotContain(entries, path => path.Contains("App.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Single(entries);
    }

    [Theory]
    [InlineData("**/Intermediate/**", "Plugins/Module/Intermediate/cache.bin")]
    [InlineData("**/.import/**", "addons/tool/.import/asset.import")]
    [InlineData("**/RenderCache/**", "episodes/scene/RenderCache/frame.tmp")]
    public void FilterService_NestedGeneratedOutputPatternsExcludeExpectedFiles(string pattern, string generatedPath)
    {
        var projectRoot = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        string generatedFile = Path.Combine(projectRoot, generatedPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(generatedFile)!);
        File.WriteAllText(Path.Combine(projectRoot, "source.txt"), "keep");
        File.WriteAllText(generatedFile, "exclude");

        var scanner = new ScannerService(new FilterService([pattern]));
        var entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("source.txt", entries);
        Assert.DoesNotContain(entries, path => path.Equals(generatedPath, StringComparison.OrdinalIgnoreCase));
        Assert.Single(entries);
    }

    [Theory]
    [InlineData("avalonia", "src/App/bin/Release/app.dll")]
    [InlineData("dotnet", "src/App/obj/project.assets.json")]
    [InlineData("node", "web/node_modules/package/index.js")]
    [InlineData("python", "module/__pycache__/tool.cpython-310.pyc")]
    [InlineData("unity", "Library/metadata/cache.bin")]
    [InlineData("unreal", "Intermediate/Build/cache.bin")]
    public void BuiltInSourcePresets_ExcludeGeneratedOutputsAndLiveGitInternals(string preset, string generatedPath)
    {
        var projectRoot = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);

        WriteProjectFile(projectRoot, "src/source.txt", "keep");
        WriteProjectFile(projectRoot, ".github/workflows/ci.yml", "workflow");
        WriteProjectFile(projectRoot, ".gitignore", "bin/");
        WriteProjectFile(projectRoot, ".gitattributes", "* text=auto");
        WriteProjectFile(projectRoot, ".git/refs/heads/local-feature", "commit");
        WriteProjectFile(projectRoot, ".git/objects/aa/object", "object");
        WriteProjectFile(projectRoot, generatedPath, "generated");

        var filter = FilterService.FromPresetAndLocal(projectRoot, preset, ResolveRepoPresetsDir());
        var scanner = new ScannerService(filter);
        var entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("src/source.txt", entries);
        Assert.Contains(".github/workflows/ci.yml", entries);
        Assert.Contains(".gitignore", entries);
        Assert.Contains(".gitattributes", entries);
        Assert.DoesNotContain(".git/refs/heads/local-feature", entries);
        Assert.DoesNotContain(".git/objects/aa/object", entries);
        Assert.DoesNotContain(entries, path => path.Equals(generatedPath, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("generic", ".blueprints/cache/state.json")]
    [InlineData("generic", "tools/.pytest_cache/nodeids")]
    [InlineData("generic", "web/.next/cache/bundle.bin")]
    [InlineData("avalonia", "log/session.log")]
    [InlineData("avalonia", "tools/.ruff_cache/index")]
    [InlineData("python", ".pytest_cache/nodeids")]
    [InlineData("python", "module/.mypy_cache/types.json")]
    [InlineData("python", "module/.ruff_cache/index")]
    [InlineData("node", ".next/cache/bundle.bin")]
    [InlineData("node", "packages/app/.turbo/state.json")]
    [InlineData("node", "packages/app/.parcel-cache/data.bin")]
    public void BuiltInPresets_ExcludeModernGeneratedState(string preset, string generatedPath)
    {
        string projectRoot = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        WriteProjectFile(projectRoot, "src/source.txt", "keep");
        WriteProjectFile(projectRoot, generatedPath, "generated");

        var scanner = new ScannerService(FilterService.FromPresetAndLocal(projectRoot, preset, ResolveRepoPresetsDir()));
        string[] entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("src/source.txt", entries);
        Assert.DoesNotContain(entries, path => path.Equals(generatedPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EditorPresets_KeepShareableConfigurationAndExcludeLocalState()
    {
        string projectRoot = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);
        WriteProjectFile(projectRoot, ".vscode/settings.json", "{}");
        WriteProjectFile(projectRoot, ".vscode/tasks.json", "{}");
        WriteProjectFile(projectRoot, ".vscode/.browse.VC.db", "generated");
        WriteProjectFile(projectRoot, ".idea/modules.xml", "<project />");
        WriteProjectFile(projectRoot, ".idea/workspace.xml", "generated");

        string[] vscodeEntries = new ScannerService(
                FilterService.FromPresetAndLocal(projectRoot, "vscode", ResolveRepoPresetsDir()))
            .Scan(projectRoot).Select(entry => entry.RelPath).ToArray();
        string[] jetBrainsEntries = new ScannerService(
                FilterService.FromPresetAndLocal(projectRoot, "jetbrains", ResolveRepoPresetsDir()))
            .Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains(".vscode/settings.json", vscodeEntries);
        Assert.Contains(".vscode/tasks.json", vscodeEntries);
        Assert.DoesNotContain(".vscode/.browse.VC.db", vscodeEntries);
        Assert.Contains(".idea/modules.xml", jetBrainsEntries);
        Assert.DoesNotContain(".idea/workspace.xml", jetBrainsEntries);
    }

    [Fact]
    public void PresetCatalog_IsCompleteAndUsesSupportedExclusionRules()
    {
        string presetsDir = ResolveRepoPresetsDir();
        string indexPath = Path.Combine(presetsDir, "presets.index.json");
        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(indexPath));

        Assert.Equal(2, index.RootElement.GetProperty("version").GetInt32());
        string[] indexedFiles = index.RootElement.GetProperty("presets")
            .EnumerateArray()
            .Select(preset => preset.GetProperty("file").GetString()!)
            .ToArray();
        string[] presetFiles = Directory.GetFiles(presetsDir, "*.vaultsyncignore")
            .Select(file => Path.GetFileName(file)!)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(indexedFiles.Length, indexedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(presetFiles, indexedFiles.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (string presetFile in presetFiles)
        {
            string[] rules = File.ReadAllLines(Path.Combine(presetsDir, presetFile));
            Assert.DoesNotContain(rules, rule => rule.TrimStart().StartsWith('!'));
        }
    }

    private static void WriteProjectFile(string projectRoot, string relativePath, string contents)
    {
        string fullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string ResolveRepoPresetsDir()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "src", "presets");
            if (Directory.Exists(candidate))
                return candidate;

            string parent = Directory.GetParent(dir)?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(parent))
                break;

            dir = parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/presets from test output.");
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
