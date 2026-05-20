using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VaultSync.Core.Services
{
    /// <summary>
    /// Lightweight JSON-lines logger.
    /// Each call writes one JSON object per line into ~/.vaultsync/logs/YYYY-MM-DD.log
    /// Safe for multi-process append. Cross-platform. No external deps.
    /// </summary>
    public static class Logger
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Write a log event.
        /// </summary>
        /// <param name="command">Human friendly command description (e.g., "snapshot VaultTest").</param>
        /// <param name="result">"ok" | "error" | "warn" | any short status.</param>
        /// <param name="details">Anonymous object with extra data (counts, durations, exit codes, etc.).</param>
        public static void Write(string command, string result, object? details = null)
        {
            try
            {
                DateTime nowUtc = DateTime.UtcNow;
                var lineObj = new
                {
                    time = nowUtc.ToString("O"), // ISO-8601 with timezone (UTC)
                    command,
                    result,
                    details
                };

                string json = JsonSerializer.Serialize(lineObj, JsonOptions);
                string path = GetTodayLogPath(nowUtc);

                EnsureDirectoryExists(Path.GetDirectoryName(path)!);

                // Append atomically-ish: open/append/close
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                sw.WriteLine(json);
            }
            catch
            {
                // Never throw from logger - swallow to avoid breaking CLI flows.
                // In a debug build you could Debug.WriteLine(ex) if desired.
            }
        }

        /// <summary>
        /// Compute today’s log file path under ~/.vaultsync/logs/.
        /// </summary>
        private static string GetTodayLogPath(DateTime utcNow)
        {
            string baseDir = GetVaultBaseDir();
            string logsDir = Path.Combine(baseDir, "logs");
            string file = $"{utcNow:yyyy-MM-dd}.log";
            return Path.Combine(logsDir, file);
        }

        /// <summary>
        /// Returns ~/.vaultsync on macOS/Linux, %USERPROFILE%\.vaultsync on Windows.
        /// </summary>
        private static string GetVaultBaseDir()
        {
            // Prefer HOME on Unix-like; USERPROFILE on Windows
            string? home =
                Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(home))
                home = ".";

            return Path.Combine(home, ".vaultsync");
        }

        private static void EnsureDirectoryExists(string dir)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}