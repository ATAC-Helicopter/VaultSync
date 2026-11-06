using System;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

/// <summary>Serializes page-location writes and coalesces queued locations to the newest one.</summary>
public sealed class PageLocationWriter
{
    private readonly object _gate = new();
    private readonly Func<string, Task> _write;
    private readonly Action<Exception> _reportFailure;
    private string? _pending;
    private TaskCompletionSource? _completion;

    public PageLocationWriter(Func<string, Task> write, Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(reportFailure);
        _write = write;
        _reportFailure = reportFailure;
    }

    public Task Enqueue(string page)
    {
        if (!LegacyDesktopRouteAdapter.TryFromLegacy(page, out _))
            throw new ArgumentException("Unsupported page location.", nameof(page));
        lock (_gate)
        {
            _pending = page;
            if (_completion is not null)
                return _completion.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            _ = Task.Run(() => DrainAsync(completion));
            return completion.Task;
        }
    }

    private async Task DrainAsync(TaskCompletionSource completion)
    {
        while (true)
        {
            string page;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _completion = null;
                    completion.SetResult();
                    return;
                }
                page = _pending;
                _pending = null;
            }
            try
            {
                await _write(page).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { _reportFailure(ex); }
                catch (Exception) { /* A failed diagnostic callback must not strand the queue. */ }
            }
        }
    }
}
