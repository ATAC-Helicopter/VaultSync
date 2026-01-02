using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace VaultSync.UI
{
    /// <summary>
    /// Resolves *View for a given *ViewModel by name convention.
    /// Works without any ViewModelBase, and avoids crashing on strings/nulls.
    /// </summary>
    public sealed class ViewLocator : IDataTemplate
    {
        public Control? Build(object? data)
        {
            if (data is null) return new ContentControl();

            // If a view instance was passed, just use it.
            if (data is Control c) return c;

            // Avoid the previous crash when data was a string
            if (data is string s) return new TextBlock { Text = s };

            var vmType = data.GetType();

            // Map "VaultSync.UI.ViewModels.XxxViewModel"
            //   -> "VaultSync.UI.Views.XxxView"
            var viewTypeName = vmType.FullName?
                .Replace(".ViewModels.", ".Views.")
                .Replace("ViewModel", "View");

            // Try to resolve the view type from the current assembly first.
            var asm = Assembly.GetExecutingAssembly();
            var viewType =
                (viewTypeName is not null ? asm.GetType(viewTypeName) : null)
                ?? (viewTypeName is not null ? Type.GetType(viewTypeName) : null);

            if (viewType is not null && Activator.CreateInstance(viewType) is Control view)
            {
                view.DataContext = data;
                return view;
            }

            // Fallback so the app doesn’t explode if a mapping is missing
            return new TextBlock { Text = $"View not found for {vmType.Name}" };
        }

        public bool Match(object? data)
        {
            if (data is null)
                return false;

            if (data is Control || data is string)
                return true;

            var name = data.GetType().FullName ?? string.Empty;
            return name.Contains(".ViewModels.", StringComparison.Ordinal) ||
                   name.EndsWith("ViewModel", StringComparison.Ordinal);
        }
    }
}
