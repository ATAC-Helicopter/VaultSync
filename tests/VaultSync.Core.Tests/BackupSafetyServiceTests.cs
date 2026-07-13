using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Models;
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
    public void BuiltInSourcePresets_ExcludeGeneratedOutputsButKeepRepoMetadata(string preset, string generatedPath)
    {
        var projectRoot = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectRoot);

        WriteProjectFile(projectRoot, "src/source.txt", "keep");
        WriteProjectFile(projectRoot, ".github/workflows/ci.yml", "workflow");
        WriteProjectFile(projectRoot, ".gitignore", "bin/");
        WriteProjectFile(projectRoot, ".gitattributes", "* text=auto");
        WriteProjectFile(projectRoot, generatedPath, "generated");

        var filter = FilterService.FromPresetAndLocal(projectRoot, preset, ResolveRepoPresetsDir());
        var scanner = new ScannerService(filter);
        var entries = scanner.Scan(projectRoot).Select(entry => entry.RelPath).ToArray();

        Assert.Contains("src/source.txt", entries);
        Assert.Contains(".github/workflows/ci.yml", entries);
        Assert.Contains(".gitignore", entries);
        Assert.Contains(".gitattributes", entries);
        Assert.DoesNotContain(entries, path => path.Equals(generatedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteProjectFile(string projectRoot, string relativePath, string contents)
    {
        string fullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
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
