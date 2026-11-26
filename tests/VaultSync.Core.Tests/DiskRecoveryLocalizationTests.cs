#nullable enable
using System.IO;
using System.Threading.Tasks;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskRecoveryLocalizationTests
{
    [Fact]
    public async Task FolderSelectionDistinguishesAnImageFromItsParentAndKeepsLocalization()
    {
        using var temp = new TempDirectory();
        var image = Directory.CreateDirectory(Path.Combine(temp.Path, "exported"));
        File.WriteAllText(Path.Combine(image.FullName, "image.metadata"), string.Empty);
        File.WriteAllText(Path.Combine(image.FullName, "payload.raw"), string.Empty);
        var previous = LocalizationProvider.Service;
        var localization = new LocalizationService();
        try
        {
            LocalizationProvider.Initialize(localization);
            var model = new DiskCloningViewModel();
            await model.DiskRecovery.SelectFolderAsync(temp.Path);
            Assert.False(model.DiskRecovery.VerifyCommand.CanExecute(null));
            var english = model.DiskRecovery.Status;
            Assert.True(localization.SetLanguage("it"));
            model.DiskRecovery.ReapplyLocalization();
            Assert.Equal(temp.Path, model.DiskRecovery.Folder);
            Assert.NotEqual(english, model.DiskRecovery.Status);
            Assert.Equal(localization.GetString(model.DiskRecovery.Supported ? "DiskRecovery.MissingFiles" : "DiskRecovery.Unsupported"),
                model.DiskRecovery.Status);
            Assert.False(model.DiskRecovery.HasImageFiles);

            await model.DiskRecovery.SelectFolderAsync(image.FullName);
            Assert.True(model.DiskRecovery.HasImageFiles);
            Assert.Equal(localization.GetString(model.DiskRecovery.Supported ? "DiskRecovery.Ready" : "DiskRecovery.Unsupported"),
                model.DiskRecovery.Status);
            Assert.False(model.DiskRecovery.CancelCommand.CanExecute(null));
            Assert.Equal(model.DiskRecovery.Supported, model.DiskRecovery.VerifyCommand.CanExecute(null));
        }
        finally { LocalizationProvider.Initialize(previous!); }
    }
}
