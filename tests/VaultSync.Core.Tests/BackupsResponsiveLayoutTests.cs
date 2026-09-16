using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupsResponsiveLayoutTests
{
    [Theory]
    [InlineData(1200, 3)]
    [InlineData(1049, 2)]
    [InlineData(800, 2)]
    [InlineData(619, 1)]
    [InlineData(420, 1)]
    public void ResponsiveLayout_UsesReadableSummaryColumns(
        double width,
        int summaryColumns)
    {
        BackupsView.ResponsiveLayout layout = BackupsView.GetResponsiveLayout(width);

        Assert.Equal(summaryColumns, layout.SummaryColumns);
    }
}
