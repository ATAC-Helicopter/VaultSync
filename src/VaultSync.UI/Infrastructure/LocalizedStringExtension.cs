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
            return new Binding
            {
                Source = new LocalizedStringValueProvider(Key),
                Path = nameof(LocalizedStringValueProvider.Value),
                Mode = BindingMode.OneWay
            };
        }

        private sealed class LocalizedStringValueProvider : INotifyPropertyChanged
        {
            private readonly string _key;
            private LocalizationService? _service;

            public LocalizedStringValueProvider(string key)
            {
                _key = key;
                LocalizationProvider.ServiceChanged += HandleServiceChanged;
                AttachToService(LocalizationProvider.Service);
                UpdateValue();
            }

            public string Value { get; private set; } = string.Empty;

            public event PropertyChangedEventHandler? PropertyChanged;

            private void HandleServiceChanged()
            {
                AttachToService(LocalizationProvider.Service);
                UpdateValue();
            }

            private void AttachToService(LocalizationService? service)
            {
                if (_service == service)
                    return;

                if (_service is not null)
                    _service.LanguageChanged -= HandleLocalizationChanged;

                _service = service;

                if (_service is not null)
                    _service.LanguageChanged += HandleLocalizationChanged;
            }

            private void HandleLocalizationChanged()
            {
                UpdateValue();
            }

            private void UpdateValue()
            {
                string value = LocalizationProvider.Service?.GetString(_key) ?? _key;
                if (!string.Equals(Value, value, StringComparison.Ordinal))
                {
                    Value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }
        }
    }
}
