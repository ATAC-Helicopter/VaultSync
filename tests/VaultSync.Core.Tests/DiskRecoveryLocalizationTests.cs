#nullable enable
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskRecoveryLocalizationTests
{
    [Fact]
    public void LanguageChangesPreserveImageSelectionAndReapplyStatus()
    {
        var previous = LocalizationProvider.Service;
        var localization = new LocalizationService();
        try
        {
            LocalizationProvider.Initialize(localization);
            var model = new RecoveryViewModel();
            model.DiskRecovery.SelectFolder("synthetic-image-folder");
            var english = model.DiskRecovery.Status;
            Assert.True(localization.SetLanguage("it"));
            model.ReapplyLocalization();
            Assert.Equal("synthetic-image-folder", model.DiskRecovery.Folder);
            Assert.NotEqual(english, model.DiskRecovery.Status);
            Assert.Equal(localization.GetString(model.DiskRecovery.Supported ? "DiskRecovery.Ready" : "DiskRecovery.Unsupported"),
                model.DiskRecovery.Status);
            Assert.False(model.DiskRecovery.CancelCommand.CanExecute(null));
            Assert.Equal(model.DiskRecovery.Supported, model.DiskRecovery.VerifyCommand.CanExecute(null));
        }
        finally { LocalizationProvider.Initialize(previous!); }
    }
}
