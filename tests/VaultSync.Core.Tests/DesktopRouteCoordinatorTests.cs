#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DesktopRouteCoordinatorTests
{
    private static readonly DesktopRoute Home = DesktopRoute.ProtectOverview;
    private static readonly DesktopRoute Projects = new(DesktopRouteKind.Protect, DesktopRoutePage.Projects);
    private static readonly DesktopRoute History = new(DesktopRouteKind.History, DesktopRoutePage.Overview);
    private static readonly DesktopRoute Settings = new(DesktopRouteKind.Manage, DesktopRoutePage.Settings);

    [Fact]
    public async Task OpeningAndHistoryAreCommittedOnlyAfterResolution()
    {
        TaskCompletionSource<bool> pending = NewCompletion();
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => pending.Task);
        Task<DesktopNavigationResult> open = coordinator.OpenAsync(Projects);
        Assert.Equal(Home, coordinator.Current);
        Assert.False(coordinator.CanGoBack);
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Opened, await open);
        Assert.Equal(Projects, coordinator.Current);
        Assert.True(coordinator.CanGoBack);
        Assert.Equal(DesktopNavigationResult.Opened, await coordinator.BackAsync());
        Assert.Equal(Home, coordinator.Current);
        Assert.Equal(DesktopNavigationResult.Opened, await coordinator.ForwardAsync());
        Assert.Equal(Projects, coordinator.Current);
    }

    [Fact]
    public async Task MissingHistoryTargetPreservesCurrentAndCanBeRetried()
    {
        bool available = true;
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => Task.FromResult(available));
        await coordinator.OpenAsync(Projects);
        available = false;
        Assert.Equal(DesktopNavigationResult.Unavailable, await coordinator.BackAsync());
        Assert.Equal(Projects, coordinator.Current);
        Assert.True(coordinator.CanGoBack);
        Assert.False(coordinator.CanGoForward);
        available = true;
        Assert.Equal(DesktopNavigationResult.Opened, await coordinator.BackAsync());
        available = false;
        Assert.Equal(DesktopNavigationResult.Unavailable, await coordinator.ForwardAsync());
        Assert.Equal(Home, coordinator.Current);
        Assert.True(coordinator.CanGoForward);
    }

    [Fact]
    public async Task NewBranchClearsForwardOnlyWhenItSuccessfullyOpens()
    {
        bool available = true;
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => Task.FromResult(available));
        await coordinator.OpenAsync(Projects);
        await coordinator.OpenAsync(History);
        await coordinator.BackAsync();
        available = false;
        Assert.Equal(DesktopNavigationResult.Unavailable, await coordinator.OpenAsync(Settings));
        Assert.True(coordinator.CanGoForward);
        available = true;
        await coordinator.OpenAsync(Settings);
        Assert.False(coordinator.CanGoForward);
        Assert.Equal(DesktopNavigationResult.NoHistory, await coordinator.ForwardAsync());
    }

    [Fact]
    public async Task HistoryRetainsOnlyTheConfiguredNumberOfPreviousLocations()
    {
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => Task.FromResult(true), historyLimit: 2);
        await coordinator.OpenAsync(Projects);
        await coordinator.OpenAsync(History);
        await coordinator.OpenAsync(Settings);
        await coordinator.BackAsync();
        Assert.Equal(History, coordinator.Current);
        await coordinator.BackAsync();
        Assert.Equal(Projects, coordinator.Current);
        Assert.Equal(DesktopNavigationResult.NoHistory, await coordinator.BackAsync());
        await coordinator.ForwardAsync();
        await coordinator.ForwardAsync();
        Assert.Equal(Settings, coordinator.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowRequestCannotOverwriteNewerNavigation(bool newerTargetIsUnavailable)
    {
        TaskCompletionSource<bool> pending = NewCompletion();
        var coordinator = new DesktopRouteCoordinator(Home, (route, _) =>
            route == Projects ? pending.Task : Task.FromResult(!newerTargetIsUnavailable));
        Task<DesktopNavigationResult> older = coordinator.OpenAsync(Projects);
        Assert.Equal(newerTargetIsUnavailable ? DesktopNavigationResult.Unavailable : DesktopNavigationResult.Opened,
            await coordinator.OpenAsync(History));
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Superseded, await older);
        Assert.Equal(newerTargetIsUnavailable ? Home : History, coordinator.Current);
        Assert.Equal(!newerTargetIsUnavailable, coordinator.CanGoBack);
    }

    [Fact]
    public async Task StayingOnCurrentLocationSupersedesAnOlderPendingRequest()
    {
        TaskCompletionSource<bool> pending = NewCompletion();
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => pending.Task);
        Task<DesktopNavigationResult> older = coordinator.OpenAsync(Projects);
        Assert.Equal(DesktopNavigationResult.AlreadyCurrent, await coordinator.OpenAsync(Home));
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Superseded, await older);
        Assert.Equal(Home, coordinator.Current);
        Assert.False(coordinator.CanGoBack);
    }

    [Fact]
    public async Task OverlappingBackRequestsDoNotConsumeHistoryTwice()
    {
        TaskCompletionSource<bool>? pending = null;
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => pending?.Task ?? Task.FromResult(true));
        await coordinator.OpenAsync(Projects);
        await coordinator.OpenAsync(History);
        pending = NewCompletion();
        Task<DesktopNavigationResult> older = coordinator.BackAsync();
        Task<DesktopNavigationResult> newer = coordinator.BackAsync();
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Superseded, await older);
        Assert.Equal(DesktopNavigationResult.Opened, await newer);
        Assert.Equal(Projects, coordinator.Current);
        pending = null;
        await coordinator.BackAsync();
        Assert.Equal(Home, coordinator.Current);
        await coordinator.ForwardAsync();
        await coordinator.ForwardAsync();
        Assert.Equal(History, coordinator.Current);
    }

    [Fact]
    public async Task CancellationStillPreventsCommitWhenResolverIgnoresItsToken()
    {
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource<bool> pending = NewCompletion();
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => pending.Task);
        Task<DesktopNavigationResult> request = coordinator.OpenAsync(Projects, cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Cancelled, await request);
        Assert.Equal(Home, coordinator.Current);
        Assert.False(coordinator.CanGoBack);
    }

    [Fact]
    public async Task CancelledBackLeavesBothHistoryStacksIntact()
    {
        using var cancellation = new CancellationTokenSource();
        var coordinator = new DesktopRouteCoordinator(Home, (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        });
        await coordinator.OpenAsync(Projects);
        cancellation.Cancel();
        Assert.Equal(DesktopNavigationResult.Cancelled, await coordinator.BackAsync(cancellation.Token));
        Assert.Equal(Projects, coordinator.Current);
        Assert.True(coordinator.CanGoBack);
        Assert.False(coordinator.CanGoForward);
    }

    [Fact]
    public async Task ResolverCancellationIsReportedOnlyForTheRequestedToken()
    {
        using var cancellation = new CancellationTokenSource();
        var coordinator = new DesktopRouteCoordinator(Home, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(token);
        });
        Assert.Equal(DesktopNavigationResult.Cancelled, await coordinator.OpenAsync(Projects, cancellation.Token));
        Assert.Equal(Home, coordinator.Current);
        var unrelatedCancellation = new DesktopRouteCoordinator(Home,
            (_, _) => Task.FromException<bool>(new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => unrelatedCancellation.OpenAsync(Projects));
        Assert.Equal(Home, unrelatedCancellation.Current);
    }

    [Fact]
    public async Task ResolverErrorsLeaveStateIntactAndRemainVisibleToTheCaller()
    {
        bool fail = false;
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => fail
            ? Task.FromException<bool>(new InvalidOperationException("Resolution failed")) : Task.FromResult(true));
        await coordinator.OpenAsync(Projects);
        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.BackAsync());
        Assert.Equal(Projects, coordinator.Current);
        Assert.True(coordinator.CanGoBack);
        Assert.False(coordinator.CanGoForward);
    }

    [Fact]
    public async Task UnsupportedAndEmptyHistoryRequestsDoNotCallTheResolver()
    {
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => throw new InvalidOperationException("Unexpected resolution"));
        Assert.Equal(DesktopNavigationResult.Unsupported, await coordinator.OpenAsync(default));
        Assert.Equal(DesktopNavigationResult.NoHistory, await coordinator.BackAsync());
        Assert.Equal(DesktopNavigationResult.NoHistory, await coordinator.ForwardAsync());
        Assert.Equal(DesktopNavigationResult.AlreadyCurrent, await coordinator.OpenAsync(Home));
        Assert.Equal(Home, coordinator.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void InvalidHistoryLimitsAreRejected(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopRouteCoordinator(Home, (_, _) => Task.FromResult(true), limit));

    [Fact]
    public void InvalidInitialRoutesAndMissingResolversAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new DesktopRouteCoordinator(default, (_, _) => Task.FromResult(true)));
        Assert.Throws<ArgumentNullException>(() => new DesktopRouteCoordinator(Home, null!));
    }

    [Fact]
    public async Task ClearingHistoryStartsAtTheCurrentLocationAndSupersedesPendingNavigation()
    {
        TaskCompletionSource<bool>? pending = null;
        var coordinator = new DesktopRouteCoordinator(Home, (_, _) => pending?.Task ?? Task.FromResult(true));
        await coordinator.OpenAsync(Projects);
        await coordinator.OpenAsync(History);
        await coordinator.BackAsync();
        pending = NewCompletion();
        Task<DesktopNavigationResult> older = coordinator.OpenAsync(Settings);
        coordinator.ClearHistory();
        Assert.Equal(Projects, coordinator.Current);
        Assert.False(coordinator.CanGoBack);
        Assert.False(coordinator.CanGoForward);
        pending.SetResult(true);
        Assert.Equal(DesktopNavigationResult.Superseded, await older);
        pending = null;
        await coordinator.OpenAsync(Settings);
        await coordinator.BackAsync();
        Assert.Equal(Projects, coordinator.Current);
    }

    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
