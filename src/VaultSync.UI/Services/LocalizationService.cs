using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace VaultSync.UI.Services
{
    public sealed class LanguageOption
    {
        public string Code { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    public sealed class LocalizationService : INotifyPropertyChanged
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LanguageOption> _languageOptions = new()
        {
            new LanguageOption { Code = "en", DisplayName = "English" },
            new LanguageOption { Code = "it", DisplayName = "Italiano" },
            new LanguageOption { Code = "es", DisplayName = "Español" },
            new LanguageOption { Code = "fr", DisplayName = "Français" },
            new LanguageOption { Code = "de", DisplayName = "Deutsch" },
            new LanguageOption { Code = "pt", DisplayName = "Português" },
            new LanguageOption { Code = "zh", DisplayName = "简体中文" },
            new LanguageOption { Code = "hi", DisplayName = "हिन्दी" },
            new LanguageOption { Code = "ar", DisplayName = "العربية" },
            new LanguageOption { Code = "bn", DisplayName = "বাংলা" },
            new LanguageOption { Code = "ru", DisplayName = "Русский" }
        };

        private string _pendingLanguage = "en";

        public LocalizationService()
        {
            CurrentLanguage = "en";
            LoadLanguage(CurrentLanguage);
        }

        public IReadOnlyList<LanguageOption> SupportedLanguages => _languageOptions;

        public string CurrentLanguage { get; private set; }

        public event Action? LanguageChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool SetLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            var normalized = code.Trim();
            if (string.Equals(normalized, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _pendingLanguage = normalized;
                return true;
            }

            if (!LoadLanguage(normalized))
                return false;

            CurrentLanguage = normalized;
            _pendingLanguage = normalized;
            LanguageChanged?.Invoke();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            // Signal all bindings (including sidebar/localized resources) to refresh.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            return true;
        }

        public string GetString(string key)
        {
            if (_cache.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var value))
                return value;

            if (_cache.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enValue))
                return enValue;

            return key;
        }

        public string this[string key] => GetString(key);

        private bool LoadLanguage(string code)
        {
            if (_cache.ContainsKey(code))
                return true;

            var path = Path.Combine(AppContext.BaseDirectory, "Localization", $"strings.{code}.json");
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(path);
                var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
                _cache[code] = dictionary;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static class LocalizationProvider
    {
        public static LocalizationService? Service { get; private set; }

        public static void Initialize(LocalizationService service)
        {
            Service = service;
        }
    }
}
