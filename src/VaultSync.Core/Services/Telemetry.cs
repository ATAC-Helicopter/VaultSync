using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace VaultSync.Core.Services;

/// <summary>
/// Anonymised telemetry logger that never writes user content (paths, filenames, usernames).
/// All free-form strings are hashed with a per-install salt; lengths are kept for debugging.
/// Events are stored locally as JSON-lines under ~/.vaultsync/telemetry/YYYY-MM-DD.ndjson.
/// </summary>
public static class Telemetry
{
    private const string TelemetryDirectoryName = "telemetry";

    private static volatile bool _enabled;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object InitLock = new();
    private static readonly string InstallId = LoadOrCreateInstallId();
    private static readonly string Salt = LoadOrCreateSalt();
    private static Guid _sessionId = Guid.NewGuid();

    /// <summary>
    /// Enable or disable telemetry (opt-in only).
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public static bool IsEnabled => _enabled;

    /// <summary>
    /// Allows the host app to set a session identifier (new Guid each app start).
    /// </summary>
    public static void SetSessionId(Guid? sessionId = null)
    {
        _sessionId = sessionId ?? Guid.NewGuid();
    }

    /// <summary>
    /// Write an anonymised telemetry event.
    /// </summary>
    public static void Log(string name, Action<TelemetryEventBuilder>? build = null)
    {
        if (!_enabled)
            return;

        try
        {
            // Opportunistically prune old telemetry before writing.
            PruneOldLogs();

            var builder = new TelemetryEventBuilder(Salt);
            build?.Invoke(builder);

            var evt = new
            {
                time = DateTime.UtcNow.ToString("O"),
                name,
                installId = InstallId,
                sessionId = _sessionId,
                payload = builder.ToPayload()
            };

            string json = JsonSerializer.Serialize(evt, JsonOptions);
            string path = GetTodayLogPath(DateTime.UtcNow);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.WriteLine(json);
        }
        catch
        {
            // Telemetry must never throw.
        }
    }

    private static string GetTodayLogPath(DateTime utcNow)
    {
        string baseDir = GetVaultBaseDir();
        string logsDir = Path.Combine(baseDir, TelemetryDirectoryName);
        string file = $"{utcNow:yyyy-MM-dd}.ndjson";
        return Path.Combine(logsDir, file);
    }

    /// <summary>
    /// Export telemetry files into a zip archive for manual sharing (opt-in, local only).
    /// </summary>
    public static TelemetryExportResult ExportToZip(string? targetDirectory = null)
    {
        try
        {
            string logsDir = GetTelemetryDirectory();
            if (!Directory.Exists(logsDir))
            {
                return new TelemetryExportResult(false, null, "No telemetry directory found.");
            }

            string[] files = Directory.GetFiles(logsDir, "*.ndjson", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return new TelemetryExportResult(false, null, "No telemetry files to export.");
            }

            string destDir = string.IsNullOrWhiteSpace(targetDirectory)
                ? Path.Combine(Path.GetTempPath(), "vaultsync-telemetry-export")
                : targetDirectory;
            Directory.CreateDirectory(destDir);

            string zipName = $"telemetry_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
            string zipPath = Path.Combine(destDir, zipName);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(logsDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return new TelemetryExportResult(true, zipPath, "Telemetry exported.");
        }
        catch (Exception ex)
        {
            return new TelemetryExportResult(false, null, $"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the telemetry directory path (may not exist).
    /// </summary>
    public static string GetTelemetryDirectory()
    {
        string baseDir = GetVaultBaseDir();
        return Path.Combine(baseDir, TelemetryDirectoryName);
    }

    /// <summary>
    /// Best-effort cleanup of old telemetry to avoid unbounded growth.
    /// Keeps files newer than RetentionDays and caps total size.
    /// </summary>
    private static void PruneOldLogs(int retentionDays = 30, long maxTotalBytes = 20 * 1024 * 1024)
    {
        try
        {
            string baseDir = GetVaultBaseDir();
            string logsDir = Path.Combine(baseDir, TelemetryDirectoryName);
            if (!Directory.Exists(logsDir))
                return;

            var files = Directory.GetFiles(logsDir, "*.ndjson", SearchOption.TopDirectoryOnly)
                                 .Select(path => new FileInfo(path))
                                 .OrderByDescending(f => f.LastWriteTimeUtc)
                                 .ToList();

            DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (FileInfo? file in files.Where(f => f.LastWriteTimeUtc < cutoff))
            {
                try { file.Delete(); } catch { }
            }

            // Recompute after date-based prune
            files = [.. Directory.GetFiles(logsDir, "*.ndjson", SearchOption.TopDirectoryOnly)
                             .Select(path => new FileInfo(path))
                             .OrderByDescending(f => f.LastWriteTimeUtc)];

            if (maxTotalBytes > 0)
            {
                long total = files.Sum(f => f.Length);
                foreach (FileInfo? file in files.Where(f => total > maxTotalBytes).OrderBy(f => f.LastWriteTimeUtc))
                {
                    try
                    {
                        total -= file.Length;
                        file.Delete();
                    }
                    catch
                    {
                        // continue best-effort
                    }
                }
            }
        }
        catch
        {
            // best-effort; never throw
        }
    }

    private static string LoadOrCreateInstallId()
    {
        lock (InitLock)
        {
            try
            {
                string baseDir = GetVaultBaseDir();
                string path = Path.Combine(baseDir, TelemetryDirectoryName, "installation.id");
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (Guid.TryParse(existing, out Guid parsed))
                        return parsed.ToString();
                }

                string id = Guid.NewGuid().ToString();
                File.WriteAllText(path, id);
                return id;
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }
    }

    private static string LoadOrCreateSalt()
    {
        lock (InitLock)
        {
            try
            {
                string baseDir = GetVaultBaseDir();
                string path = Path.Combine(baseDir, TelemetryDirectoryName, "telemetry.salt");
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(existing))
                        return existing;
                }

                byte[] bytes = new byte[32];
                RandomNumberGenerator.Fill(bytes);
                string salt = HashService.FormatHexLower(bytes);
                File.WriteAllText(path, salt);
                return salt;
            }
            catch
            {
                // Fall back to a volatile salt if persistence fails.
                byte[] bytes = new byte[32];
                RandomNumberGenerator.Fill(bytes);
                return HashService.FormatHexLower(bytes);
            }
        }
    }

    /// <summary>
    /// Returns ~/.vaultsync on macOS/Linux, %USERPROFILE%\.vaultsync on Windows.
    /// </summary>
    private static string GetVaultBaseDir()
    {
        string? home =
            Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(home))
            home = ".";

        return Path.Combine(home, ".vaultsync");
    }
}

public sealed record TelemetryExportResult(bool Success, string? ZipPath, string? Message);

public sealed class TelemetryEventBuilder
{
    private readonly Dictionary<string, object?> _payload = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _salt;

    internal TelemetryEventBuilder(string salt)
    {
        _salt = salt;
    }

    public TelemetryEventBuilder WithFlag(string key, bool value)
    {
        _payload[key] = value;
        return this;
    }

    public TelemetryEventBuilder WithCount(string key, int value)
    {
        _payload[key] = value;
        return this;
    }

    public TelemetryEventBuilder WithNumber(string key, double? value)
    {
        if (value.HasValue)
            _payload[key] = value.Value;
        return this;
    }

    /// <summary>
    /// Stores only a hash and length for any potentially sensitive string (path, project name, etc.).
    /// </summary>
    public TelemetryEventBuilder WithHashedString(string key, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return this;

        _payload[key] = new
        {
            hash = Hash(raw),
            length = raw.Length
        };
        return this;
    }

    public TelemetryEventBuilder WithException(Exception ex)
    {
        if (ex is null)
            return this;

        _payload["error"] = new
        {
            type = ex.GetType().Name,
            messageHash = Hash(ex.Message ?? string.Empty)
        };
        return this;
    }

    public TelemetryEventBuilder WithCode(string key, string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            _payload[key] = code;
        return this;
    }

    internal IReadOnlyDictionary<string, object?> ToPayload() => _payload;

    private string Hash(string raw)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_salt));
        byte[] bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return HashService.FormatHexLower(bytes);
    }
}
