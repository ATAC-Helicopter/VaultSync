using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ScheduleResponsiveLayoutTests
{
    [Theory]
    [InlineData(1200, false, false)]
    [InlineData(759, true, false)]
    [InlineData(559, true, true)]
    public void ResponsiveLayout_StacksAtExpectedWidths(
        double width,
        bool stackOptions,
        bool stackOverview)
    {
        ScheduleView.ResponsiveLayout layout = ScheduleView.GetResponsiveLayout(width);

        Assert.Equal(stackOptions, layout.StackOptions);
        Assert.Equal(stackOverview, layout.StackOverview);
    }
}
