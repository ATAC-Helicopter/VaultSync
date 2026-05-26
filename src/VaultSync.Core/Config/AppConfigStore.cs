using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.Core.Services;


namespace VaultSync.Core.Config
{
    public static class AppConfigStore
    {
        private const int ConfigWriteMaxAttempts = 5;
        private const int ConfigWriteInitialDelayMs = 40;
        private const int ConfigReadMaxAttempts = 5;
        private const int ConfigReadInitialDelayMs = 25;

        private static readonly SemaphoreSlim SaveGate = new(1, 1);
        private static readonly object LastKnownGoodGate = new();
        private static readonly object ConfigPathGate = new();
        private static string? TestConfigDirOverride;
        private static int _firstLoadState; // 0=unknown, 1=missing config, 2=existing config

        private static string ConfigDir
        {
            get
            {
                lock (ConfigPathGate)
                {
                    return TestConfigDirOverride ?? ResolveConfigDir();
                }
            }
        }

        private static string ConfigFilePath =>
            Path.Combine(ConfigDir, "appsettings.json");

        private static string ConfigBackupFilePath =>
            Path.Combine(ConfigDir, "appsettings.bak.json");

        private static AppConfig? LastKnownGoodConfig;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static bool WasConfigMissingOnFirstLoad => Volatile.Read(ref _firstLoadState) == 1;

        public static IDisposable UseDirectoryForTests(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Config directory is required.", nameof(directory));

            string fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullPath);

            string? previousOverride;
            lock (ConfigPathGate)
            {
                previousOverride = TestConfigDirOverride;
                TestConfigDirOverride = fullPath;
            }

            ResetRuntimeStateForTests();
            return new TestConfigDirectoryScope(previousOverride);
        }

        public static AppConfig GetSnapshot()
        {
            return GetLastKnownGoodClone() ?? Load();
        }

        public static AppConfig Load()
        {
            try
            {
                AppConfig cfg;

                // If config file does not exist, create a new default config
                if (!File.Exists(ConfigFilePath))
                {
                    TrySetFirstLoadState(missingConfig: true);
                    cfg = new AppConfig();
                }
                else
                {
                    TrySetFirstLoadState(missingConfig: false);
                    cfg = LoadBestAvailableConfig();
                }

                // ----- Ensure DbPath is always set -----
                if (string.IsNullOrWhiteSpace(cfg.DbPath))
                {
                    cfg.DbPath = GetDefaultDbPath();

                    // Save updated config with the new DbPath (best-effort; ignore failures so app can still run)
                    try
                    {
                        Save(cfg);
                    }
                    catch (Exception ex)
                    {
                        RecordConfigDiagnostic("Failed to persist default DbPath", ex);
                    }
                }

                RememberLastKnownGood(cfg);
                RuntimeLog.UpdateFromConfig(cfg);
                return cfg;
            }
            catch (Exception ex)
            {
                // On any error, fall back to defaults instead of crashing the app.
                RecordConfigDiagnostic("Load failed; using fallback config", ex);
                AppConfig fallback = GetLastKnownGoodClone() ?? new AppConfig();

                // Ensure fallback also receives a default DbPath
                fallback.DbPath = GetDefaultDbPath();

                RuntimeLog.UpdateFromConfig(fallback);
                return fallback;
            }
        }

        private static void TrySetFirstLoadState(bool missingConfig)
        {
            int state = missingConfig ? 1 : 2;
            Interlocked.CompareExchange(ref _firstLoadState, state, 0);
        }

        public static void Save(AppConfig config)
        {
            Directory.CreateDirectory(ConfigDir);
            PreserveDurableConfigValues(config);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            WriteConfigWithRetry(json);
            RememberLastKnownGood(config);
            RuntimeLog.UpdateFromConfig(config);
        }

        public static async Task SaveAsync(AppConfig config, CancellationToken ct = default)
        {
            Directory.CreateDirectory(ConfigDir);
            PreserveDurableConfigValues(config);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            await WriteConfigWithRetryAsync(json, ct).ConfigureAwait(false);
            RememberLastKnownGood(config);
            RuntimeLog.UpdateFromConfig(config);
        }

        public static string GetDefaultDbPath()
        {
            return Path.Combine(GetDefaultDataDir(), "vaultsync.db");
        }

        public static string ResolveDbPath(AppConfig? config = null)
        {
            AppConfig cfg = config ?? GetSnapshot();
            return string.IsNullOrWhiteSpace(cfg.DbPath)
                ? GetDefaultDbPath()
                : cfg.DbPath;
        }

        private static void WriteConfigWithRetry(string json)
        {
            int delay = ConfigWriteInitialDelayMs;

            SaveGate.Wait();
            try
            {
                for (int attempt = 1; attempt <= ConfigWriteMaxAttempts; attempt++)
                {
                    try
                    {
                        WriteConfigOnce(json);
                        return;
                    }
                    catch (IOException) when (attempt < ConfigWriteMaxAttempts)
                    {
                        Thread.Sleep(delay);
                        delay *= 2;
                    }
                }

                WriteConfigOnce(json);
            }
            finally
            {
                SaveGate.Release();
            }
        }

        private static async Task WriteConfigWithRetryAsync(string json, CancellationToken ct)
        {
            int delay = ConfigWriteInitialDelayMs;

            await SaveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 1; attempt <= ConfigWriteMaxAttempts; attempt++)
                {
                    try
                    {
                        WriteConfigOnce(json);
                        return;
                    }
                    catch (IOException) when (attempt < ConfigWriteMaxAttempts)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        delay *= 2;
                    }
                }

                // Last attempt: allow exception to surface for diagnostics.
                WriteConfigOnce(json);
            }
            finally
            {
                SaveGate.Release();
            }
        }

        private static void WriteConfigOnce(string json)
        {
            string tempPath = Path.Combine(ConfigDir, $"appsettings.tmp.{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(ConfigFilePath))
                {
                    File.Replace(tempPath, ConfigFilePath, ConfigBackupFilePath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, ConfigFilePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static string ReadConfigWithRetry(string path)
        {
            int delay = ConfigReadInitialDelayMs;

            for (int attempt = 1; attempt <= ConfigReadMaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < ConfigReadMaxAttempts)
                {
                    Thread.Sleep(delay);
                    delay *= 2;
                }
            }

            return File.ReadAllText(path);
        }

        private static string GetDefaultDataDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "VaultSync");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static AppConfig LoadBestAvailableConfig()
        {
            try
            {
                string json = ReadConfigWithRetry(ConfigFilePath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch (Exception primaryEx)
            {
                RecordConfigDiagnostic("Primary config load failed", primaryEx);
                if (File.Exists(ConfigBackupFilePath))
                {
                    try
                    {
                        string backupJson = ReadConfigWithRetry(ConfigBackupFilePath);
                        AppConfig config = JsonSerializer.Deserialize<AppConfig>(backupJson, JsonOptions) ?? new AppConfig();
                        RecordConfigDiagnostic("Loaded backup config after primary config failure");
                        return config;
                    }
                    catch (Exception backupEx)
                    {
                        RecordConfigDiagnostic("Backup config load failed", backupEx);
                        AppConfig? cached = GetLastKnownGoodClone();
                        if (cached is not null)
                        {
                            RecordConfigDiagnostic("Using last-known-good config after backup config failure");
                            return cached;
                        }
                    }
                }

                AppConfig? fallback = GetLastKnownGoodClone();
                if (fallback is not null)
                {
                    RecordConfigDiagnostic("Using last-known-good config after primary config failure");
                    return fallback;
                }

                throw;
            }
        }

        private static void RecordConfigDiagnostic(string message, Exception? ex = null)
        {
            string detail = ex is null
                ? message
                : $"{message}: {ex.GetType().Name} - {ex.Message}";

            Console.WriteLine($"[AppConfigStore] {detail}");
        }

        private static void PreserveDurableConfigValues(AppConfig config)
        {
            PreserveProjectsRoot(config);
            PreserveMetadataImportCache(config);
        }

        private static void PreserveProjectsRoot(AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.ProjectsRoot))
                return;

            AppConfig? persisted = TryLoadPersistedConfigForPreservation(ConfigFilePath)
                            ?? TryLoadPersistedConfigForPreservation(ConfigBackupFilePath)
                            ?? GetLastKnownGoodClone();
            if (string.IsNullOrWhiteSpace(persisted?.ProjectsRoot))
                return;

            config.ProjectsRoot = persisted.ProjectsRoot.Trim();
            RuntimeLog.WriteVerbose("[Config] Save preserved existing ProjectsRoot because the pending save had an empty value.");
        }

        private static void PreserveMetadataImportCache(AppConfig config)
        {
            config.Advanced ??= new AdvancedConfig();
            config.Advanced.MetadataImportCache ??= new MetadataImportCacheConfig();
            if (config.Advanced.MetadataImportCache.Sources.Count > 0)
                return;

            AppConfig? persisted = TryLoadPersistedConfigForPreservation(ConfigFilePath)
                            ?? TryLoadPersistedConfigForPreservation(ConfigBackupFilePath)
                            ?? GetLastKnownGoodClone();
            List<MetadataImportSourceStamp>? persistedSources = persisted?
                .Advanced?
                .MetadataImportCache?
                .Sources;
            if (persistedSources is not { Count: > 0 })
                return;

            config.Advanced.MetadataImportCache.Sources = CloneMetadataImportSources(persistedSources);
            RuntimeLog.WriteVerbose("[Config] Save preserved metadata import cache because the pending save had no cache entries.");
        }

        private static List<MetadataImportSourceStamp> CloneMetadataImportSources(IEnumerable<MetadataImportSourceStamp> sources)
        {
            return sources
                .Select(source => new MetadataImportSourceStamp
                {
                    SourceKey = source.SourceKey,
                    SourcePath = source.SourcePath,
                    SourceMachineId = source.SourceMachineId,
                    StoreUpdatedUtc = source.StoreUpdatedUtc,
                    StoreSchemaVersion = source.StoreSchemaVersion,
                    StoreFileLengthBytes = source.StoreFileLengthBytes,
                    StoreFileUpdatedUtc = source.StoreFileUpdatedUtc,
                    StoreSidecarStamp = source.StoreSidecarStamp,
                    ImportedUtc = source.ImportedUtc,
                    ProjectCount = source.ProjectCount,
                    SnapshotCount = source.SnapshotCount,
                    BackupCount = source.BackupCount,
                    TombstoneCount = source.TombstoneCount,
                    ProjectExternalIds = [.. source.ProjectExternalIds],
                    SnapshotExternalIds = [.. source.SnapshotExternalIds],
                    BackupExternalIds = [.. source.BackupExternalIds]
                })
                .ToList();
        }

        private static AppConfig? TryLoadPersistedConfigForPreservation(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                string json = ReadConfigWithRetry(path);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static void RememberLastKnownGood(AppConfig config)
        {
            AppConfig clone = CloneConfig(config);
            lock (LastKnownGoodGate)
            {
                LastKnownGoodConfig = clone;
            }
        }

        private static AppConfig? GetLastKnownGoodClone()
        {
            lock (LastKnownGoodGate)
            {
                return LastKnownGoodConfig is null ? null : CloneConfig(LastKnownGoodConfig);
            }
        }

        private static AppConfig CloneConfig(AppConfig config)
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }

        private static void ResetRuntimeStateForTests()
        {
            Volatile.Write(ref _firstLoadState, 0);
            lock (LastKnownGoodGate)
            {
                LastKnownGoodConfig = null;
            }
        }

        private sealed class TestConfigDirectoryScope(string? previousOverride) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;

                lock (ConfigPathGate)
                {
                    TestConfigDirOverride = previousOverride;
                }

                ResetRuntimeStateForTests();
                _disposed = true;
            }
        }

        private static string ResolveConfigDir()
        {
            string? overrideDir = Environment.GetEnvironmentVariable("VAULTSYNC_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;

            if (IsRunningUnderTest())
            {
                return Path.Combine(
                    Path.GetTempPath(),
                    "vaultsync-test-config",
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vaultsync");
        }

        private static bool IsRunningUnderTest()
        {
            string entryName = Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;
            if (entryName.Contains("test", StringComparison.OrdinalIgnoreCase))
                return true;

            string baseDir = AppContext.BaseDirectory ?? string.Empty;
            return baseDir.Contains("test", StringComparison.OrdinalIgnoreCase);
        }
    }
}
