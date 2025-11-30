using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup;
using Avalonia.Markup.Xaml;
using VaultSync.UI.Services;

namespace VaultSync.UI.Infrastructure
{
    public sealed class LocalizedStringExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (LocalizationProvider.Service is null)
                return Key;

            return new Binding
            {
                Source = LocalizationProvider.Service,
                Path = $"[{Key}]",
                Mode = BindingMode.OneWay
            };
        }
    }
}
