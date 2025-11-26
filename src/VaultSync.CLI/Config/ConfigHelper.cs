using System;
using System.IO;
using System.Text.Json;
using VaultSync.Core.Config;

namespace VaultSync.CLI.Config
{
    /// <summary>
    /// CLI-facing config helper that now delegates to the shared Core AppConfigStore
    /// to keep the database path and settings unified across CLI and UI.
    /// </summary>
    static class ConfigHelper
    {
        private sealed class LegacyCliConfig
        {
            public string Database { get; set; } = string.Empty;
        }

        public static string GetConfigDir()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".vaultsync");
        }

        public static string GetConfigPath() => Path.Combine(GetConfigDir(), "config.json");

        public static void Save(AppConfig cfg)
        {
            var dir = GetConfigDir();
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "logs"));

            if (string.IsNullOrWhiteSpace(cfg.DbPath))
                cfg.DbPath = AppConfigStore.GetDefaultDbPath();

            AppConfigStore.Save(cfg);
        }

        public static AppConfig Load()
        {
            // Load the shared config first.
            var cfg = AppConfigStore.Load();

            // If a legacy CLI config exists with a Database value, migrate it into DbPath.
            var legacy = TryLoadLegacy();
            if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.Database))
            {
                var expanded = ExpandHome(legacy.Database);
                if (string.IsNullOrWhiteSpace(cfg.DbPath))
                {
                    cfg.DbPath = expanded;
                    AppConfigStore.Save(cfg);
                }
            }

            return cfg;
        }

        public static string ResolveDb(string? overridePath)
        {
            // 1. Explicit override wins.
            if (!string.IsNullOrWhiteSpace(overridePath))
                return ExpandHome(overridePath);

            // 2. Shared AppConfig.DbPath (ensures a single DB location for CLI + UI).
            var cfg = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(cfg.DbPath))
                return ExpandHome(cfg.DbPath);

            // 3. Legacy CLI config fallback.
            var legacy = TryLoadLegacy();
            if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.Database))
                return ExpandHome(legacy.Database);

            // 4. Safe default from shared store.
            return AppConfigStore.GetDefaultDbPath();
        }

        private static string ExpandHome(string path)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Replace("~", home);
        }

        private static LegacyCliConfig? TryLoadLegacy()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<LegacyCliConfig>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
