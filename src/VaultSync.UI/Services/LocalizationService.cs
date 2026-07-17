using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
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
        private readonly List<LanguageOption> _languageOptions =
        [
            new LanguageOption { Code = "en", DisplayName = "English" },
            new LanguageOption { Code = "it", DisplayName = "Italiano" },
            new LanguageOption { Code = "es", DisplayName = "Español" },
            new LanguageOption { Code = "fr", DisplayName = "Français" },
            new LanguageOption { Code = "de", DisplayName = "Deutsch" },
            new LanguageOption { Code = "pt", DisplayName = "Português" },
            new LanguageOption { Code = "zh", DisplayName = "中文" },
            new LanguageOption { Code = "hi", DisplayName = "हिन्दी" },
            new LanguageOption { Code = "ar", DisplayName = "العربية" },
            new LanguageOption { Code = "bn", DisplayName = "বাংলা" },
            new LanguageOption { Code = "ru", DisplayName = "Русский" },
            new LanguageOption { Code = "id", DisplayName = "Bahasa Indonesia" },
            new LanguageOption { Code = "ja", DisplayName = "日本語" },
            new LanguageOption { Code = "ko", DisplayName = "한국어" },
            new LanguageOption { Code = "nl", DisplayName = "Nederlands" },
            new LanguageOption { Code = "pl", DisplayName = "Polski" },
            new LanguageOption { Code = "tr", DisplayName = "Türkçe" },
            new LanguageOption { Code = "uk", DisplayName = "Українська" },
            new LanguageOption { Code = "vi", DisplayName = "Tiếng Việt" }
        ];
        public LocalizationService()
        {
            CurrentLanguage = "en";
            LoadLanguage(CurrentLanguage);
            WarmCache();
        }

        public IReadOnlyList<LanguageOption> SupportedLanguages => _languageOptions;

        public string CurrentLanguage { get; private set; }

        public event Action? LanguageChanging;
        public event Action? LanguageChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool SetLanguage(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string normalized = code.Trim();
            if (string.Equals(normalized, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!LoadLanguage(normalized))
                return false;

            LanguageChanging?.Invoke();
            CurrentLanguage = normalized;
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
            if (_cache.TryGetValue(CurrentLanguage, out IReadOnlyDictionary<string, string>? dict) && dict.TryGetValue(key, out string? value))
                return value;

            if (_cache.TryGetValue("en", out IReadOnlyDictionary<string, string>? enDict) && enDict.TryGetValue(key, out string? enValue))
                return enValue;

            return key;
        }

        public string this[string key] => GetString(key);

        private bool LoadLanguage(string code)
        {
            if (_cache.ContainsKey(code))
                return true;

            string path = Path.Combine(AppContext.BaseDirectory, "Localization", $"strings.{code}.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Localization] Missing file: {path}");
                return false;
            }

            try
            {
                string? json = ReadTextWithFallback(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    Console.WriteLine($"[Localization] Empty or unreadable file: {path}");
                    return false;
                }
                Dictionary<string, string> dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? [];
                _cache[code] = dictionary;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Localization] Failed to parse: {path} ({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        private void WarmCache()
        {
            foreach (LanguageOption option in _languageOptions)
            {
                if (!LoadLanguage(option.Code))
                    Console.WriteLine($"[Localization] Failed to load language: {option.Code}");
            }
        }

        private static string? ReadTextWithFallback(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var reader = new StreamReader(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch
            {
                try
                {
                    return Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(path));
                }
                catch
                {
                    try
                    {
                        return Encoding.Latin1.GetString(File.ReadAllBytes(path));
                    }
                    catch
                    {
                        return null;
                    }
                }
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
