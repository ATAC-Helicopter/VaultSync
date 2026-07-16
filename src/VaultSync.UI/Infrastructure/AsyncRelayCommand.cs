using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// An asynchronous command that observes failures and prevents overlapping execution.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly string _operationName;
    private int _isExecuting;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        string? operationName = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _operationName = string.IsNullOrWhiteSpace(operationName) ? "async-command" : operationName;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

    public Exception? LastException { get; private set; }

    public bool CanExecute(object? parameter) => !IsExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
            return;

        LastException = null;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected command outcome.
        }
        catch (Exception ex)
        {
            LastException = ex;
            DiagnosticsLogger.RecordException($"Async command failed ({_operationName})", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}
