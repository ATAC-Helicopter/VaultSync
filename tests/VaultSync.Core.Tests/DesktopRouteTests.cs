#nullable enable

using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DesktopRouteTests
{
    [Theory]
    [InlineData("Dashboard", DesktopRouteKind.Protect, DesktopRoutePage.Overview)]
    [InlineData("Projects", DesktopRouteKind.Protect, DesktopRoutePage.Projects)]
    [InlineData("Backups", DesktopRouteKind.Protect, DesktopRoutePage.Backups)]
    [InlineData("Schedule", DesktopRouteKind.Protect, DesktopRoutePage.Schedule)]
    [InlineData("History", DesktopRouteKind.History, DesktopRoutePage.Overview)]
    [InlineData("Recovery", DesktopRouteKind.Recover, DesktopRoutePage.Overview)]
    [InlineData("Guide", DesktopRouteKind.Manage, DesktopRoutePage.Guide)]
    [InlineData("Settings", DesktopRouteKind.Manage, DesktopRoutePage.Settings)]
    public void EveryExistingPageRoundTripsWithoutLosingItsIdentity(
        string key, DesktopRouteKind kind, DesktopRoutePage page)
    {
        Assert.True(LegacyDesktopRouteAdapter.TryFromLegacy(key, out DesktopRoute route));
        Assert.Equal(new DesktopRoute(kind, page), route);
        Assert.True(LegacyDesktopRouteAdapter.TryGetLegacyKey(route, out string? restored));
        Assert.Equal(key, restored);
        Assert.Equal(route, LegacyDesktopRouteAdapter.RestoreLegacyLocation(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("Settings ")]
    [InlineData("../../Settings")]
    [InlineData("vaultsync://recover/private")]
    [InlineData("FuturePage")]
    public void UnsupportedRequestsAreExplicitAndOnlySavedLocationsFallBack(string? key)
    {
        Assert.False(LegacyDesktopRouteAdapter.TryFromLegacy(key, out DesktopRoute rejected));
        Assert.False(LegacyDesktopRouteAdapter.TryGetLegacyKey(rejected, out string? legacy));
        Assert.Null(legacy);
        Assert.Equal(DesktopRoute.ProtectOverview, LegacyDesktopRouteAdapter.RestoreLegacyLocation(key));
    }

    [Theory]
    [InlineData(DesktopRouteKind.Protect, DesktopRoutePage.Overview, 0)]
    [InlineData(DesktopRouteKind.Protect, DesktopRoutePage.Overview, 2)]
    [InlineData(DesktopRouteKind.Manage, DesktopRoutePage.Overview, 1)]
    [InlineData(DesktopRouteKind.History, DesktopRoutePage.Settings, 1)]
    [InlineData(DesktopRouteKind.Recover, DesktopRoutePage.Backups, 1)]
    [InlineData((DesktopRouteKind)99, DesktopRoutePage.Overview, 1)]
    [InlineData(DesktopRouteKind.Protect, (DesktopRoutePage)99, 1)]
    public void UnsupportedVersionsAndTargetsNeverResolveToAnotherPage(
        DesktopRouteKind kind, DesktopRoutePage page, int version)
    {
        Assert.False(LegacyDesktopRouteAdapter.TryGetLegacyKey(new DesktopRoute(kind, page, version), out string? key));
        Assert.Null(key);
    }
}
