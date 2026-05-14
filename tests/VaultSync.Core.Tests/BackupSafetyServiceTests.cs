using System;
using System.IO;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupSafetyServiceTests : IDisposable
{
    private readonly string _tempDir;

    public BackupSafetyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-safety-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksBackupRootInsideProjectRoot()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
        var backupRoot = Path.Combine(projectRoot, ".vaultsync-temp-backups");
        Directory.CreateDirectory(projectRoot);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot));

        Assert.Contains("inside the project root", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksProjectRootInsideBackupRoot()
    {
        var backupRoot = Path.Combine(_tempDir, "backups");
        var projectRoot = Path.Combine(backupRoot, "project");
        Directory.CreateDirectory(projectRoot);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot));

        Assert.Contains("project root is inside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSafeBackupRoot_BlocksSameDirectory()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);

        Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, projectRoot));
    }

    [Fact]
    public void EnsureSafeBackupRoot_AllowsSiblingBackupRoot()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
        var backupRoot = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(backupRoot);

        BackupSafetyService.EnsureSafeBackupRoot(projectRoot, backupRoot);
    }

    [Fact]
    public void GetOfflineStagingRoot_IsOutsideProjectRoot()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        var project = new Project { Id = 42, Name = "Project", RootPath = projectRoot, Preset = string.Empty };

        var stagingRoot = BackupSafetyService.GetOfflineStagingRoot(project);

        BackupSafetyService.EnsureSafeBackupRoot(project, stagingRoot);
        Assert.DoesNotContain(projectRoot, stagingRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterService_AlwaysExcludesVaultSyncBackupArtifacts()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
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
    public void FilterService_DoubleStarDirectoryPatternExcludesNestedBuildOutput()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
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
        var projectRoot = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
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

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
