using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SettingsPersistencePolicyTests
{
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
