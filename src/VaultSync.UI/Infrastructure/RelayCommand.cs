using System;
using System.Windows.Input;
using Avalonia.Threading;

namespace VaultSync.UI.Infrastructure
{
    /// <summary>
    /// Minimal ICommand implementation for button bindings.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

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
}
