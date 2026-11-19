using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using VaultSync.Core.Config;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class GuideResponsiveLayoutTests
{
    [Fact]
    public async Task Refresh_UpdatesProgressWithoutReplacingTheFiveGuideCards()
    {
        using var config = new TestAppConfigScope();
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuideTestApp));
        await session.Dispatch(async () =>
        {
            // Shell navigation is unused here. Avoid starting the app's backup and metadata jobs.
            var shell = (AppViewModel)RuntimeHelpers.GetUninitializedObject(typeof(AppViewModel));
            var store = StaticAppConfigStore.Instance;
            var guide = new GuideViewModel(shell, store, new SqliteRepositoryFactory(store));
            var originalTopics = guide.Topics.ToArray();
            var originalTerms = guide.Terms.ToArray();
            string initialProgress = guide.ProgressSummary;

            await Task.WhenAll(Task.Run(guide.RefreshAsync), Task.Run(guide.RefreshAsync));

            Assert.NotEqual(initialProgress, guide.ProgressSummary);
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, guide.Topics.Select(topic => topic.Number));
            Assert.Equal(originalTopics, guide.Topics, ReferenceEqualityComparer.Instance);
            Assert.Equal(originalTerms, guide.Terms, ReferenceEqualityComparer.Instance);
            return 0;
        }, CancellationToken.None);
    }

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

    public sealed class GuideTestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<GuideTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
