using System;
using System.Collections.Generic;
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

    [Fact]
    public async Task CopyLinesAsync_PreservesVisibleSelectionOrder()
    {
        var service = new LogConsoleService();
        var viewModel = new LogConsoleViewModel(service);
        var first = new LogLine(
            new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero),
            "diagnostics",
            "first");
        var second = new LogLine(
            new DateTimeOffset(2026, 5, 14, 12, 0, 1, TimeSpan.Zero),
            "diagnostics",
            "second");
        string copiedText = string.Empty;

        viewModel.SetCopyTextAsync(text =>
        {
            copiedText = text;
            return Task.FromResult(true);
        });

        bool copied = await viewModel.CopyLinesAsync(new List<LogLine> { first, second });

        Assert.True(copied);
        Assert.Equal(
            string.Join(Environment.NewLine, first.RawDisplay, second.RawDisplay),
            copiedText);
        Assert.Contains("2", viewModel.StatusMessage, StringComparison.Ordinal);
    }
}
