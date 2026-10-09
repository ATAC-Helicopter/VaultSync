using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

public enum DesktopNavigationResult
{
    Opened,
    AlreadyCurrent,
    Unsupported,
    Unavailable,
    Cancelled,
    Superseded,
    NoHistory
}

/// <summary>
/// Review prototype: resolves page-only routes before committing in-memory navigation.
/// The resolver must be read-only. This does not dispatch UI changes, persist state,
/// authorize operations or cancel background work owned by a page.
/// </summary>
public sealed class DesktopRouteCoordinator
{
    private readonly object _gate = new();
    private readonly Func<DesktopRoute, CancellationToken, Task<bool>> _resolve;
    private readonly int _historyLimit;
    private readonly List<DesktopRoute> _back = new();
    private readonly List<DesktopRoute> _forward = new();
    private DesktopRoute _current;
    private long _requestVersion;

    public DesktopRouteCoordinator(
        DesktopRoute initialRoute,
        Func<DesktopRoute, CancellationToken, Task<bool>> resolve,
        int historyLimit = 64)
    {
        if (!LegacyDesktopRouteAdapter.TryGetLegacyKey(initialRoute, out _))
            throw new ArgumentException("Initial route is unsupported.", nameof(initialRoute));
        ArgumentNullException.ThrowIfNull(resolve);
        if (historyLimit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(historyLimit));
        _current = initialRoute;
        _resolve = resolve;
        _historyLimit = historyLimit;
    }

    public DesktopRoute Current { get { lock (_gate) return _current; } }
    public bool CanGoBack { get { lock (_gate) return _back.Count > 0; } }
    public bool CanGoForward { get { lock (_gate) return _forward.Count > 0; } }

    public Task<DesktopNavigationResult> OpenAsync(DesktopRoute route, CancellationToken cancellationToken = default) =>
        NavigateAsync(route, NavigationDirection.Open, cancellationToken);

    public Task<DesktopNavigationResult> BackAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(default, NavigationDirection.Back, cancellationToken);

    public Task<DesktopNavigationResult> ForwardAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(default, NavigationDirection.Forward, cancellationToken);

    private async Task<DesktopNavigationResult> NavigateAsync(
        DesktopRoute target, NavigationDirection direction, CancellationToken cancellationToken)
    {
        long version;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return DesktopNavigationResult.Cancelled;
            if (direction != NavigationDirection.Open)
            {
                List<DesktopRoute> history = direction == NavigationDirection.Back ? _back : _forward;
                if (history.Count == 0)
                    return DesktopNavigationResult.NoHistory;
                target = history[^1];
            }
            if (!LegacyDesktopRouteAdapter.TryGetLegacyKey(target, out _))
                return DesktopNavigationResult.Unsupported;
            version = ++_requestVersion;
            // An explicit request to stay also supersedes an older pending request.
            if (target == _current)
                return DesktopNavigationResult.AlreadyCurrent;
        }

        bool available;
        try
        {
            available = await _resolve(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DesktopNavigationResult.Cancelled;
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
                return DesktopNavigationResult.Cancelled;
            if (version != _requestVersion)
                return DesktopNavigationResult.Superseded;
            if (!available)
                return DesktopNavigationResult.Unavailable;

            if (direction == NavigationDirection.Back)
            {
                _back.RemoveAt(_back.Count - 1);
                Push(_forward, _current);
            }
            else if (direction == NavigationDirection.Forward)
            {
                _forward.RemoveAt(_forward.Count - 1);
                Push(_back, _current);
            }
            else
            {
                Push(_back, _current);
                _forward.Clear();
            }
            _current = target;
            return DesktopNavigationResult.Opened;
        }
    }

    private void Push(List<DesktopRoute> history, DesktopRoute route)
    {
        if (history.Count == _historyLimit)
            history.RemoveAt(0);
        history.Add(route);
    }

    private enum NavigationDirection { Open, Back, Forward }
}
