namespace VaultSync.UI.Infrastructure;

// Review prototype for VS-1910. It does not replace the active navigation coordinator.
public enum DesktopRouteKind
{
    Protect,
    History,
    Recover,
    Manage
}

public enum DesktopRoutePage
{
    Overview,
    Projects,
    Backups,
    Schedule,
    Guide,
    Settings
}

public readonly record struct DesktopRoute(
    DesktopRouteKind Kind,
    DesktopRoutePage Page,
    int SchemaVersion = 1)
{
    public static DesktopRoute ProtectOverview => new DesktopRoute(DesktopRouteKind.Protect, DesktopRoutePage.Overview);
}

/// <summary>
/// Maps existing page keys without resolving resources, changing page state or performing I/O.
/// Only restoring a saved legacy location permits fallback; an unsupported request stays explicit.
/// </summary>
public static class LegacyDesktopRouteAdapter
{
    public static bool TryFromLegacy(string? key, out DesktopRoute route)
    {
        DesktopRoute? mapped = key switch
        {
            "Dashboard" => DesktopRoute.ProtectOverview,
            "Projects" => new DesktopRoute(DesktopRouteKind.Protect, DesktopRoutePage.Projects),
            "Backups" => new DesktopRoute(DesktopRouteKind.Protect, DesktopRoutePage.Backups),
            "Schedule" => new DesktopRoute(DesktopRouteKind.Protect, DesktopRoutePage.Schedule),
            "History" => new DesktopRoute(DesktopRouteKind.History, DesktopRoutePage.Overview),
            "Recovery" => new DesktopRoute(DesktopRouteKind.Recover, DesktopRoutePage.Overview),
            "Guide" => new DesktopRoute(DesktopRouteKind.Manage, DesktopRoutePage.Guide),
            "Settings" => new DesktopRoute(DesktopRouteKind.Manage, DesktopRoutePage.Settings),
            _ => null
        };
        route = mapped.GetValueOrDefault();
        return mapped.HasValue;
    }

    public static DesktopRoute RestoreLegacyLocation(string? key) =>
        TryFromLegacy(key, out DesktopRoute route) ? route : DesktopRoute.ProtectOverview;

    public static bool TryGetLegacyKey(DesktopRoute route, out string? key)
    {
        key = route.SchemaVersion == 1 ? (route.Kind, route.Page) switch
        {
            (DesktopRouteKind.Protect, DesktopRoutePage.Overview) => "Dashboard",
            (DesktopRouteKind.Protect, DesktopRoutePage.Projects) => "Projects",
            (DesktopRouteKind.Protect, DesktopRoutePage.Backups) => "Backups",
            (DesktopRouteKind.Protect, DesktopRoutePage.Schedule) => "Schedule",
            (DesktopRouteKind.History, DesktopRoutePage.Overview) => "History",
            (DesktopRouteKind.Recover, DesktopRoutePage.Overview) => "Recovery",
            (DesktopRouteKind.Manage, DesktopRoutePage.Guide) => "Guide",
            (DesktopRouteKind.Manage, DesktopRoutePage.Settings) => "Settings",
            _ => null
        } : null;
        return key is not null;
    }
}
