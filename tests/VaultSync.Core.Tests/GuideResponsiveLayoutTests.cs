using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class GuideResponsiveLayoutTests
{
    [Theory]
    [InlineData(1200, false, 2, 3)]
    [InlineData(900, false, 2, 2)]
    [InlineData(719, true, 1, 2)]
    [InlineData(619, true, 1, 1)]
    public void ResponsiveLayout_UsesReadableColumns(
        double width,
        bool stackHeader,
        int topicColumns,
        int termColumns)
    {
        GuideView.ResponsiveLayout layout = GuideView.GetResponsiveLayout(width);

        Assert.Equal(stackHeader, layout.StackHeader);
        Assert.Equal(topicColumns, layout.TopicColumns);
        Assert.Equal(termColumns, layout.TermColumns);
    }
}
