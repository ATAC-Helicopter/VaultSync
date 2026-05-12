using System;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace VaultSync.CLI.Utils
{
    // Note: original had 'file sealed class' (typo) - fixed to 'sealed class'
    public sealed class AsyncDebouncer(int delayMs)
    {
        private readonly int _delayMs = Math.Max(0, delayMs);
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;

        public void Trigger(Func<CancellationToken, Task> work)
        {
            CancellationTokenSource? toCancel;
            lock (_gate)
            {
                toCancel = _cts;
                _cts = new CancellationTokenSource();
                toCancel?.Cancel();
            }

            CancellationTokenSource localCts = _cts!;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_delayMs, localCts.Token);
                    await work(localCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]watch error:[/] {Markup.Escape(ex.Message)}");
                }
            });
        }

        public void Cancel()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts = null;
            }
        }
    }
}
