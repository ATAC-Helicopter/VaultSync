using System;
using System.IO;
using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SettingsPersistencePolicyTests
{
    [Fact]
    public void FolderPickerStartCandidates_PreferCurrentFolderThenHome()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-picker-{Guid.NewGuid():N}");
        string current = Path.Combine(root, "current");
        string home = Path.Combine(root, "home");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(home);

        try
        {
            var candidates = SettingsViewModel.BuildFolderPickerStartCandidates(current, home);

            Assert.Collection(
                candidates,
                candidate => Assert.Equal(Path.GetFullPath(current), candidate.LocalPath.TrimEnd(Path.DirectorySeparatorChar)),
                candidate => Assert.Equal(Path.GetFullPath(home), candidate.LocalPath.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.All(candidates, candidate => Assert.True(candidate.IsFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FolderPickerStartCandidates_SkipMissingAndDuplicateFolders()
    {
        string home = Path.Combine(Path.GetTempPath(), $"vaultsync-picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);

        try
        {
            Assert.Single(SettingsViewModel.BuildFolderPickerStartCandidates(Path.Combine(home, "missing"), home));
            Assert.Single(SettingsViewModel.BuildFolderPickerStartCandidates(home, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ResolveProjectsRootForSave_PreservesPersistedRootWhenRequestedRootIsBlank()
    {
        string persisted = @"D:\Projects";

        string resolved = SettingsViewModel.ResolveProjectsRootForSave("   ", persisted);

        Assert.Equal(persisted, resolved);
    }

    [Fact]
    public void ResolveProjectsRootForSave_UsesRequestedRootWhenProvided()
    {
        string resolved = SettingsViewModel.ResolveProjectsRootForSave(@" E:\Work ", @"D:\Projects");

        Assert.Equal(@"E:\Work", resolved);
    }

    [Fact]
    public void ResolveBackupRootForSave_PreservesPersistedRootWhenRequestedRootIsBlank()
    {
        string persisted = @"F:\VaultSyncBackups";

        string resolved = SettingsViewModel.ResolveBackupRootForSave(null, persisted);

        Assert.Equal(persisted, resolved);
    }

    [Fact]
    public void ResolveBackupRootForSave_AllowsEmptyWhenNoPersistedRootExists()
    {
        string resolved = SettingsViewModel.ResolveBackupRootForSave("", "");

        Assert.Null(resolved);
    }

    [Fact]
    public void ShouldAutoSaveProperty_IgnoresStatusOnlyProperties()
    {
        Assert.False(SettingsViewModel.ShouldAutoSaveProperty(nameof(SettingsViewModel.SaveStatus)));
        Assert.False(SettingsViewModel.ShouldAutoSaveProperty(nameof(SettingsViewModel.UpdateDiagnosticsText)));
        Assert.False(SettingsViewModel.ShouldAutoSaveProperty(nameof(SettingsViewModel.BackupLocationStatus)));
    }

    [Fact]
    public void ShouldAutoSaveProperty_AllowsEditableRootProperties()
    {
        Assert.True(SettingsViewModel.ShouldAutoSaveProperty(nameof(SettingsViewModel.ProjectsRootPath)));
        Assert.True(SettingsViewModel.ShouldAutoSaveProperty(nameof(SettingsViewModel.BackupLocationPath)));
    }
}
