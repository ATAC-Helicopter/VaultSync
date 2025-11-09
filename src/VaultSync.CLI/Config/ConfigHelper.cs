using System;
using System.IO;
using System.Text.Json;

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
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath.Replace("~", home);

            var cfg = Load();
            return cfg.Database.Replace("~", home);
        }
    }
}