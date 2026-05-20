using System;
using System.Threading.Tasks;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LogConsoleViewModelTests
{
    [Fact]
    public async Task CopySelectedLineAsync_UsesConfiguredCopyHandler()
    {
        var service = new LogConsoleService();
        var viewModel = new LogConsoleViewModel(service);
        var line = new LogLine(
            new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            "diagnostics",
            "copy me");
        string copiedText = string.Empty;

        viewModel.SetCopyTextAsync(text =>
        {
            copiedText = text;
            return Task.FromResult(true);
        });
        viewModel.SelectedLine = line;

        bool copied = await viewModel.CopySelectedLineAsync();

        Assert.True(copied);
        Assert.Equal(line.RawDisplay, copiedText);
    }
}
