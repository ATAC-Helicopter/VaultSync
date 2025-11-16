using System;
using System.IO;
using System.Text.Json;
using VaultSync.Core.Config;

namespace VaultSync.CLI.Config
{
    static class ConfigHelper
    {
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

            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetConfigPath(), json + Environment.NewLine);
        }

        public static AppConfig Load()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
                throw new Exception("Run `vaultsync init` first (creates ~/.vaultsync/config.json)");

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            return cfg;
        }

        public static string ResolveDb(string? overridePath)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 1. If an explicit override is provided, honor it.
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath.Replace("~", home);

            // 2. Prefer the shared Core AppConfig.DbPath if available.
            try
            {
                var coreConfig = AppConfigStore.Load();
                if (!string.IsNullOrWhiteSpace(coreConfig.DbPath))
                    return coreConfig.DbPath.Replace("~", home);
            }
            catch
            {
                // If Core config cannot be loaded, fall back to the legacy CLI config.
            }

            // 3. Fallback: use the legacy CLI ~/.vaultsync/config.json Database value.
            var cfg = Load();
            return cfg.Database.Replace("~", home);
        }
    }
}