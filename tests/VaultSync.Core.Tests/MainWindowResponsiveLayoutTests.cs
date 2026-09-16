using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class MainWindowResponsiveLayoutTests
{
    [Theory]
    [InlineData(600, true)]
    [InlineData(1099, true)]
    [InlineData(1100, false)]
    [InlineData(1200, false)]
    [InlineData(1450, false)]
    public void Sidebar_OnlyAutoCollapsesWhenContentNeedsTheSpace(double width, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldAutoCollapseSidebar(width));
    }
}
