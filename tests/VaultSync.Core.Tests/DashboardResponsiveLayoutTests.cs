using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DashboardResponsiveLayoutTests
{
    [Theory]
    [InlineData(1200, false, false, false, false, 300)]
    [InlineData(850, true, false, false, false, 300)]
    [InlineData(650, true, true, false, false, 300)]
    [InlineData(540, true, true, true, false, 250)]
    [InlineData(460, true, true, true, true, 250)]
    [InlineData(360, true, true, true, true, 220)]
    public void ResponsiveLayout_StacksAndShrinksAtExpectedWidths(
        double width,
        bool stackSections,
        bool stackActivity,
        bool stackStorage,
        bool compactHeader,
        double donutSize)
    {
        DashboardView.ResponsiveLayout layout = DashboardView.GetResponsiveLayout(width);

        Assert.Equal(stackSections, layout.StackSections);
        Assert.Equal(stackActivity, layout.StackActivityContent);
        Assert.Equal(stackStorage, layout.StackStorageContent);
        Assert.Equal(compactHeader, layout.CompactStorageHeader);
        Assert.Equal(donutSize, layout.DonutHostSize);
    }
}
