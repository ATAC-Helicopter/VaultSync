using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SettingsOperationIsolationTests
{
    [Fact]
    public void RepairAndMetadataConflictBusyState_DoNotDisableUnrelatedCommands()
    {
        using var config = new TestAppConfigScope();
        var viewModel = new SettingsViewModel(new LocalizationService());
        var conflict = (SettingsViewModel.ProjectMetadataConflictItemViewModel)
            RuntimeHelpers.GetUninitializedObject(
                typeof(SettingsViewModel.ProjectMetadataConflictItemViewModel));

        SetPrivateProperty(viewModel, nameof(SettingsViewModel.IsBackupIndexRepairBusy), true);

        Assert.False(viewModel.ScanBackupIndexRepairPlanCommand.CanExecute(null));
        Assert.True(viewModel.AcceptProjectMetadataConflictCommand.CanExecute(conflict));
        Assert.True(viewModel.KeepLocalProjectMetadataConflictCommand.CanExecute(conflict));

        SetPrivateProperty(viewModel, nameof(SettingsViewModel.IsBackupIndexRepairBusy), false);
        SetPrivateProperty(viewModel, nameof(SettingsViewModel.IsMetadataConflictBusy), true);

        Assert.True(viewModel.ScanBackupIndexRepairPlanCommand.CanExecute(null));
        Assert.False(viewModel.AcceptProjectMetadataConflictCommand.CanExecute(conflict));
        Assert.False(viewModel.KeepLocalProjectMetadataConflictCommand.CanExecute(conflict));
    }

    private static void SetPrivateProperty(SettingsViewModel target, string propertyName, bool value)
    {
        PropertyInfo property = typeof(SettingsViewModel).GetProperty(propertyName)!;
        Assert.NotNull(property);
        MethodInfo setter = property.GetSetMethod(nonPublic: true)!;
        Assert.NotNull(setter);
        setter.Invoke(target, [value]);
    }
}
