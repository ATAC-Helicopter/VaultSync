using System;
using System.IO;

namespace VaultSync.CLI.Utils
{
    public static class Log
    {
        static readonly object _gate = new();
        static string? _logFile;

        public static void Init()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = Path.Combine(home, ".vaultsync", "logs");
                Directory.CreateDirectory(dir);

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd");
                _logFile = Path.Combine(dir, $"vaultsync-{stamp}.log");
                Write("INFO", "=== vaultsync start ===");
            }
            catch { /* never throw */ }
        }

        public static void Write(string level, string message)
        {
            try
            {
                if (_logFile is null) return;
                var line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
                lock (_gate) File.AppendAllText(_logFile, line);
            }
            catch { /* never throw */ }
        }

        public static void Info(string message)  => Write("INFO",  message);
        public static void Warn(string message)  => Write("WARN",  message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Exception(Exception ex, string where)
        {
            Write("ERROR", $"{where}: {ex.GetType().Name}: {ex.Message}");
            Write("ERROR", ex.StackTrace ?? "(no stack)");
        }
    }
}