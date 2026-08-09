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
            ArgumentNullException.ThrowIfNull(work);

            CancellationTokenSource? toCancel;
            CancellationTokenSource localCts;
            lock (_gate)
            {
                toCancel = _cts;
                localCts = new CancellationTokenSource();
                _cts = localCts;
            }
            toCancel?.Cancel();

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
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cts, localCts))
                            _cts = null;
                    }
                    localCts.Dispose();
                }
            }, CancellationToken.None);
        }

        public void Cancel()
        {
            CancellationTokenSource? toCancel;
            lock (_gate)
            {
                toCancel = _cts;
                _cts = null;
            }
            toCancel?.Cancel();
        }
    }
}
