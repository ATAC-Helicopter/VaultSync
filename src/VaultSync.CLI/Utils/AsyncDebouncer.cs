using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace VaultSync.CLI.Utils;

public sealed class AsyncDebouncer(int delayMs) : IAsyncDisposable
{
    private readonly int _delayMs = Math.Max(0, delayMs);
    private readonly object _gate = new();
    private readonly HashSet<Invocation> _running = [];
    private Invocation? _pending;
    private bool _stopped;

    private sealed class Invocation
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    public void Trigger(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_stopped)
                return;

            // Cancellation and disposal share this lock so superseding a completed
            // invocation cannot cancel an already disposed token source.
            _pending?.Cancellation.Cancel();
            var invocation = new Invocation();
            _pending = invocation;
            _running.Add(invocation);
            invocation.Completion = Task.Run(() => RunAsync(invocation, work));
        }
    }

    private async Task RunAsync(Invocation invocation, Func<CancellationToken, Task> work)
    {
        try
        {
            await Task.Delay(_delayMs, invocation.Cancellation.Token);
            await work(invocation.Cancellation.Token);
        }
        catch (OperationCanceledException) when (invocation.Cancellation.IsCancellationRequested)
        {
            // A newer trigger or shutdown superseded this invocation.
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]watch error:[/] {Markup.Escape(ex.Message)}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, invocation))
                    _pending = null;
                _running.Remove(invocation);
                invocation.Cancellation.Dispose();
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.Cancellation.Cancel();
            _pending = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            _stopped = true;
            foreach (Invocation invocation in _running)
                invocation.Cancellation.Cancel();
            running = _running.Select(invocation => invocation.Completion).ToArray();
            _pending = null;
        }
        await Task.WhenAll(running);
    }
}
