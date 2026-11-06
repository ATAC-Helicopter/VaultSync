#nullable enable

using System.Collections.Generic;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryLocalizationTests
{
    [Fact]
    public void LanguageChange_UpdatesCachedInitialTextAndPreservesSearchAndFilter()
    {
        LocalizationService? previous = LocalizationProvider.Service;
        var localization = new LocalizationService();
        try
        {
            LocalizationProvider.Initialize(localization);
            var viewModel = new RecoveryViewModel
            {
                ProjectSearchText = "keep my search"
            };
            viewModel.SelectedProjectFilter = viewModel.ProjectFilters[2];
            string englishHeadline = viewModel.Headline;
            var changedProperties = new List<string?>();
            viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

            Assert.True(localization.SetLanguage("it"));
            viewModel.ReapplyLocalization();

            Assert.NotEqual(englishHeadline, viewModel.Headline);
            Assert.Equal("Caricamento dello stato di ripristino...", viewModel.Headline);
            Assert.Equal("7 g", viewModel.Coverage7WindowLabel);
            Assert.Equal("keep my search", viewModel.ProjectSearchText);
            Assert.Equal(RecoveryProjectFilter.Ready, viewModel.SelectedProjectFilter!.Filter);
            Assert.Equal(localization.GetString("Recovery.Filter.Ready"), viewModel.SelectedProjectFilter.Label);
            Assert.Contains(string.Empty, changedProperties);

            Assert.True(localization.SetLanguage("en"));
            viewModel.ReapplyLocalization();
            Assert.Equal(englishHeadline, viewModel.Headline);
            Assert.Equal("7 d", viewModel.Coverage7WindowLabel);
            Assert.Equal("keep my search", viewModel.ProjectSearchText);
        }
        finally
        {
            // Restore the exact original provider, including the uninitialized state.
            LocalizationProvider.Initialize(previous!);
        }
    }
}
