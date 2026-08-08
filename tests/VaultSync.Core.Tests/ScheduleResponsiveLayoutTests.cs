using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ScheduleResponsiveLayoutTests
{
    [Theory]
    [InlineData(1200, false, false, false, false)]
    [InlineData(910, true, false, false, false)]
    [InlineData(880, true, true, false, false)]
    [InlineData(759, true, true, true, false)]
    [InlineData(619, true, true, true, true)]
    public void ResponsiveLayout_StacksAtExpectedWidths(
        double width,
        bool stackMetrics,
        bool stackOperations,
        bool stackPolicy,
        bool stackOverview)
    {
        ScheduleView.ResponsiveLayout layout = ScheduleView.GetResponsiveLayout(width);

        Assert.Equal(stackMetrics, layout.StackMetrics);
        Assert.Equal(stackOperations, layout.StackOperations);
        Assert.Equal(stackPolicy, layout.StackPolicy);
        Assert.Equal(stackOverview, layout.StackOverview);
    }
}
