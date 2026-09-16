using VaultSync.UI;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SettingsResponsiveLayoutTests
{
    [Theory]
    [InlineData(1000, false)]
    [InlineData(800, false)]
    [InlineData(799, true)]
    [InlineData(600, true)]
    public void ResponsiveLayout_StacksContentAtCompactWidths(
        double width,
        bool stackContent)
    {
        SettingsView.ResponsiveLayout layout = SettingsView.GetResponsiveLayout(width);

        Assert.Equal(stackContent, layout.StackContent);
    }
}
