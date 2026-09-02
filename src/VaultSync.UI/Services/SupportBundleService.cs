using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

public sealed record SupportBundleExportResult(bool Success, string? ZipPath, string Message);
public sealed record SupportBundleExportOptions(bool IncludeDiagnostics = true, bool IncludeTelemetry = true);
public sealed record SupportBundlePreviewItem(string RelativePath, string Category, long MaximumBytes, bool Required);
public sealed record SupportBundlePreviewResult(
    bool Success,
    IReadOnlyList<SupportBundlePreviewItem> Files,
    long MaximumBytes,
    string Message);

public sealed class SupportBundleService
{
    private const int DiagnosticsFileLimit = 8;
    private const int TelemetryFileLimit = 7;
    private const long DiagnosticsFileByteLimit = 512 * 1024;
    private const long TelemetryFileByteLimit = 256 * 1024;
    private const long BundleByteLimit = 8 * 1024 * 1024;
    private const string RedactedValue = "[redacted]";
    private const string ReportFileName = "support-report.json";
    private const string ManifestFileName = "support-manifest.json";
    private const string TelemetryExtension = ".ndjson";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Regex SensitiveJsonValue = new(
        "(?i)(\\\"(?:password|token|secret|keyRef|username|domain)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex CredentialReference = new(
        "(?i)\\bcred-[a-z0-9_-]+\\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly Regex UriUserInfo = new(
        "(?i)([a-z][a-z0-9+.-]*://)[^/@\\s]+@",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static SupportBundlePreviewResult Preview(SupportBundleExportOptions? options = null)
    {
        options ??= new SupportBundleExportOptions();
        try
        {
            IReadOnlyList<SupportBundlePreviewItem> files = BuildPreviewItems(options);
            long maximumBytes = files.Sum(file => file.MaximumBytes);
            return new SupportBundlePreviewResult(
                maximumBytes <= BundleByteLimit,
                files,
                maximumBytes,
                maximumBytes <= BundleByteLimit
                    ? "Support bundle preview ready."
                    : "Selected support bundle sections exceed the safe size limit.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SupportBundlePreviewResult(false, [], 0, $"Support bundle preview failed: {ex.Message}");
        }
    }

    public static SupportBundleExportResult Export(
        IAppConfigStore? configStore = null,
        SupportBundleExportOptions? options = null)
    {
        configStore ??= StaticAppConfigStore.Instance;
        options ??= new SupportBundleExportOptions();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        string bundleName = $"vaultsync-support-{timestamp:yyyyMMdd-HHmmss}.zip";
        string exportRoot = Path.Combine(
            GetFolderSafe(Environment.SpecialFolder.MyDocuments),
            "VaultSync",
            "Exports",
            "Support");
        string stagingRoot = Path.Combine(
            GetFolderSafe(Environment.SpecialFolder.LocalApplicationData),
            "VaultSync",
            "exports",
            $"support-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(exportRoot);
            Directory.CreateDirectory(stagingRoot);

            AppConfig config = configStore.GetSnapshot();
            object report = BuildBundleReport(config, timestamp);

            string reportJson = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(Path.Combine(stagingRoot, ReportFileName), reportJson);

            if (options.IncludeDiagnostics)
                CopySanitizedDiagnostics(stagingRoot, config);
            if (options.IncludeTelemetry)
                CopySanitizedTelemetry(stagingRoot, config);

            WriteManifest(stagingRoot, timestamp);
            long bundleBytes = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            if (bundleBytes > BundleByteLimit)
                throw new InvalidDataException("Support bundle exceeded its safe size limit.");

            string zipPath = Path.Combine(exportRoot, bundleName);
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            ZipFile.CreateFromDirectory(stagingRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            return new SupportBundleExportResult(true, zipPath, "Support bundle exported.");
        }
        catch (Exception ex)
        {
            return new SupportBundleExportResult(false, null, $"Support bundle export failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Best effort staging cleanup.
            }
        }
    }

    private static List<SupportBundlePreviewItem> BuildPreviewItems(SupportBundleExportOptions options)
    {
        var files = new List<SupportBundlePreviewItem>
        {
            new(ReportFileName, "Report", 512 * 1024, true),
            new(ManifestFileName, "Manifest", 128 * 1024, true)
        };

        if (options.IncludeDiagnostics)
        {
            files.AddRange(DiscoverTextFiles(
                GetDiagnosticsDirectory(),
                [".log", ".txt"],
                DiagnosticsFileLimit,
                DiagnosticsFileByteLimit,
                "Diagnostics",
                "diagnostics/diagnostic",
                ".log"));
        }

        if (options.IncludeTelemetry)
        {
            files.AddRange(DiscoverTextFiles(
                Telemetry.GetTelemetryDirectory(),
                [TelemetryExtension],
                TelemetryFileLimit,
                TelemetryFileByteLimit,
                "Telemetry",
                "telemetry/events",
                TelemetryExtension));
        }

        return files;
    }

    private static SupportBundlePreviewItem[] DiscoverTextFiles(
        string root,
        IReadOnlyCollection<string> extensions,
        int countLimit,
        long byteLimit,
        string category,
        string outputPrefix,
        string outputExtension)
    {
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(countLimit)
            .Select((file, index) => new SupportBundlePreviewItem(
                $"{outputPrefix}-{index + 1:00}{outputExtension}",
                category,
                Math.Min(file.Length, byteLimit),
                false))
            .ToArray();
    }

    internal static object BuildBundleReport(AppConfig config, DateTimeOffset timestamp)
    {
        object localMetadata = QueryMetadataSummary(config.DbPath);
        List<object> destinationMetadata = BuildDestinationMetadataSummary(config.Backups.Destinations);

        return new
        {
            generatedUtc = timestamp,
            app = AppBuildInformationService.Current,
            redactedConfig = BuildRedactedConfig(config),
            localMetadata,
            destinationMetadata
        };
    }

    internal static object BuildRedactedConfig(AppConfig config)
    {
        return new
        {
            projectsRootHint = RedactPath(config.ProjectsRoot),
            dbPathHint = RedactPath(config.DbPath),
            lastView = config.LastView,
            backups = new
            {
                config.Backups.EnableAutoBackups,
                config.Backups.IntervalMinutes,
                config.Backups.MaxSnapshotsPerProject,
                config.Backups.EnableMetadataSync,
                config.Backups.AutoImportMetadata,
                config.Backups.PromptRestoreAfterImport,
                config.Backups.EnableBandwidthLimit,
                config.Backups.MaxBandwidthMbps,
                config.Backups.EnableQuietHours,
                config.Backups.QuietHoursStart,
                config.Backups.QuietHoursEnd,
                config.Backups.UseAdvancedDestinations,
                backupRootHint = RedactPath(config.Backups.BackupRoot),
                config.Backups.UseCompression,
                config.Backups.UseRsyncDelta,
                config.Backups.UseIncrementalBackups,
                config.Backups.UseFullSnapshotHash,
                config.Backups.EnableScanCache,
                config.Backups.AggressiveScanCache,
                config.Backups.EnableArchiveUploadAutoTune,
                config.Backups.EnableParallelArchiveUpload,
                config.Backups.VerifyAfterCreate,
                config.Backups.PauseOnBattery,
                encryption = new
                {
                    config.Backups.Encryption.Enabled,
                    keyRef = RedactToken(config.Backups.Encryption.KeyRef),
                    config.Backups.Encryption.Algorithm,
                    config.Backups.Encryption.KdfProfile,
                    kdfParamRef = RedactToken(config.Backups.Encryption.KdfParamRef),
                    config.Backups.Encryption.AllowSessionFallback,
                    config.Backups.Encryption.OpenUnlockTimeoutMinutes
                },
                destinations = config.Backups.Destinations.Select(d => new
                {
                    alias = RedactToken(d.Alias),
                    pathHint = RedactPath(d.Path),
                    d.Active,
                    d.AutoMount,
                    d.AutoUnmount,
                    d.PreMounted,
                    credentialName = RedactToken(d.CredentialName),
                    d.EnableMetadataSync,
                    d.AutoImportMetadata,
                    d.ForceMetadataBackfill,
                    d.RetryMaxAttempts,
                    d.RetryBackoffSeconds
                }).ToList()
            },
            network = new
            {
                credentials = config.Network.Credentials.Select(c => new
                {
                    name = RedactToken(c.Name),
                    username = RedactToken(c.Username),
                    domain = RedactToken(c.Domain),
                    keyRef = RedactToken(c.KeyRef),
                    c.UseKeychain,
                    password = string.IsNullOrWhiteSpace(c.Password) ? string.Empty : RedactedValue
                }).ToList()
            },
            storage = new
            {
                config.Storage.PreferExternalDrives,
                config.Storage.ShowDriveWarnings,
                config.Storage.MinFreeSpacePercent
            },
            appearance = new
            {
                config.Appearance.Theme,
                config.Appearance.CompactLayout,
                config.Appearance.ShowProjectAvatars,
                tagColorRuleCount = config.Appearance.TagColors?.Count ?? 0
            },
            notifications = new
            {
                config.Notifications.OnBackupSuccess,
                config.Notifications.OnBackupFailure,
                config.Notifications.OnSnapshotSuccess,
                config.Notifications.OnSnapshotFailure,
                config.Notifications.OnLowDisk,
                config.Notifications.UseOsNotifications,
                config.Notifications.OnlyWhenInactive
            },
            advanced = new
            {
                config.Advanced.VerboseLogging,
                config.Advanced.SaveVerboseLogs,
                config.Advanced.CheckUpdates,
                config.Advanced.UpdateCheckIntervalMinutes,
                config.Advanced.BetaChannelEnabled,
                config.Advanced.Language,
                skippedUpdateTag = RedactToken(config.Advanced.SkippedUpdateTag),
                lastWhatsNewVersion = RedactToken(config.Advanced.LastWhatsNewVersion),
                config.Advanced.HasSeenOnboarding,
                updateDiagnostics = new
                {
                    config.Advanced.UpdateDiagnostics.CheckedUtc,
                    config.Advanced.UpdateDiagnostics.Channel,
                    config.Advanced.UpdateDiagnostics.CurrentVersion,
                    config.Advanced.UpdateDiagnostics.Decision,
                    patchPreflight = new
                    {
                        config.Advanced.UpdateDiagnostics.PatchPreflight.StatusCode,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.CurrentVersion,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.ManifestPreviousVersion,
                        manifestAllowedBaseVersions = config.Advanced.UpdateDiagnostics.PatchPreflight.ManifestAllowedBaseVersions.ToList(),
                        config.Advanced.UpdateDiagnostics.PatchPreflight.MatchedBaseVersion,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.ManifestTargetVersion,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.Eligible,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.RequiresInstaller,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.HasManifest,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.HasArchive,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.HasInstaller
                    },
                    selectedCandidate = config.Advanced.UpdateDiagnostics.SelectedCandidate is null
                        ? null
                        : new
                        {
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.Tag,
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.TargetCommitish,
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.PublishedUtc,
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.Prerelease,
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.HasPatch,
                            config.Advanced.UpdateDiagnostics.SelectedCandidate.HasInstaller
                        },
                    stableCandidate = config.Advanced.UpdateDiagnostics.StableCandidate is null
                        ? null
                        : new
                        {
                            config.Advanced.UpdateDiagnostics.StableCandidate.Tag,
                            config.Advanced.UpdateDiagnostics.StableCandidate.TargetCommitish,
                            config.Advanced.UpdateDiagnostics.StableCandidate.PublishedUtc,
                            config.Advanced.UpdateDiagnostics.StableCandidate.Prerelease,
                            config.Advanced.UpdateDiagnostics.StableCandidate.HasPatch,
                            config.Advanced.UpdateDiagnostics.StableCandidate.HasInstaller
                        },
                    betaCandidate = config.Advanced.UpdateDiagnostics.BetaCandidate is null
                        ? null
                        : new
                        {
                            config.Advanced.UpdateDiagnostics.BetaCandidate.Tag,
                            config.Advanced.UpdateDiagnostics.BetaCandidate.TargetCommitish,
                            config.Advanced.UpdateDiagnostics.BetaCandidate.PublishedUtc,
                            config.Advanced.UpdateDiagnostics.BetaCandidate.Prerelease,
                            config.Advanced.UpdateDiagnostics.BetaCandidate.HasPatch,
                            config.Advanced.UpdateDiagnostics.BetaCandidate.HasInstaller
                        }
                },
                backupRepairTelemetry = new
                {
                    config.Advanced.BackupRepairTelemetry.LastScanUtc,
                    config.Advanced.BackupRepairTelemetry.PlannedActionCount,
                    config.Advanced.BackupRepairTelemetry.BlockedIssueBucketCount,
                    plannedActionCodes = config.Advanced.BackupRepairTelemetry.PlannedActionCodes.ToList(),
                    blockedIssueCodes = config.Advanced.BackupRepairTelemetry.BlockedIssueCodes.ToList(),
                    config.Advanced.BackupRepairTelemetry.LastApplyUtc,
                    config.Advanced.BackupRepairTelemetry.LastAppliedCount,
                    config.Advanced.BackupRepairTelemetry.LastStatus
                },
                metadataConflictTelemetry = new
                {
                    config.Advanced.MetadataConflictTelemetry.LastUpdatedUtc,
                    config.Advanced.MetadataConflictTelemetry.PendingConflictCount,
                    config.Advanced.MetadataConflictTelemetry.LastResolutionAction,
                    lastResolvedProject = RedactToken(config.Advanced.MetadataConflictTelemetry.LastResolvedProject),
                    conflicts = (config.Advanced.ProjectMetadataConflicts ?? [])
                        .Select(conflict => new
                        {
                            projectIdentity = RedactToken(conflict.ProjectExternalId),
                            projectName = RedactToken(conflict.ProjectName),
                            sourceIdentity = RedactToken(conflict.SourceMachineId),
                            conflict.SourceUpdatedUtc,
                            differingFields = BuildConflictFieldList(conflict)
                        })
                        .ToList()
                },
                backupIndexLastScan = new
                {
                    config.Advanced.BackupIndexLastScan.CheckedUtc,
                    config.Advanced.BackupIndexLastScan.ProjectCount,
                    config.Advanced.BackupIndexLastScan.SnapshotCount,
                    config.Advanced.BackupIndexLastScan.BackupCount,
                    config.Advanced.BackupIndexLastScan.ErrorCount,
                    config.Advanced.BackupIndexLastScan.WarningCount,
                    topFindingCodes = config.Advanced.BackupIndexLastScan.TopFindingCodes.ToList()
                },
                startupDiagnostics = new
                {
                    config.Advanced.StartupDiagnostics.LastCompletedUtc,
                    config.Advanced.StartupDiagnostics.TotalDurationMs,
                    phases = config.Advanced.StartupDiagnostics.Phases
                        .Select(phase => new
                        {
                            phase.Name,
                            phase.ElapsedMs
                        })
                        .ToList()
                },
                checkpointResumeTelemetry = new
                {
                    config.Advanced.CheckpointResumeTelemetry.LastUpdatedUtc,
                    config.Advanced.CheckpointResumeTelemetry.LastStatus,
                    projectName = RedactToken(config.Advanced.CheckpointResumeTelemetry.LastProjectName),
                    backupFolder = RedactPath(config.Advanced.CheckpointResumeTelemetry.LastBackupFolder),
                    archivePath = RedactPath(config.Advanced.CheckpointResumeTelemetry.LastArchivePath),
                    config.Advanced.CheckpointResumeTelemetry.LastResumeOffsetBytes,
                    config.Advanced.CheckpointResumeTelemetry.LastArchiveSizeBytes,
                    sourceFingerprint = RedactToken(config.Advanced.CheckpointResumeTelemetry.LastSourceFingerprint)
                }
            },
            behavior = new
            {
                config.Behavior.RunInBackground,
                config.Behavior.ShowWindowOnTrayActions,
                config.Behavior.ShowTrayIcon,
                config.Behavior.ShowBackupWidget,
                config.Behavior.EnableSystemNotifications,
                config.Behavior.MinimizeToTray,
                config.Behavior.LaunchOnLogin,
                config.Behavior.ConfirmDeleteBackup
            }
        };
    }

    private static List<string> BuildConflictFieldList(ProjectMetadataConflictRecord conflict)
    {
        var fields = new List<string>();
        if (!string.Equals(conflict.Local?.PreferredDestinationId, conflict.Imported?.PreferredDestinationId, StringComparison.Ordinal))
            fields.Add("preferredDestination");
        if (!string.Equals(conflict.Local?.RestoreMode, conflict.Imported?.RestoreMode, StringComparison.Ordinal))
            fields.Add("restoreMode");
        if (!string.Equals(conflict.Local?.VerificationPolicy, conflict.Imported?.VerificationPolicy, StringComparison.Ordinal))
            fields.Add("verificationPolicy");
        if (!string.Equals(conflict.Local?.Tags, conflict.Imported?.Tags, StringComparison.Ordinal))
            fields.Add("tags");
        return fields;
    }

    private static object QueryMetadataSummary(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return new
            {
                status = "missing",
                dbPathHint = RedactPath(dbPath),
                projects = 0,
                snapshots = 0,
                backups = 0
            };
        }

        try
        {
            using var c = new SqliteConnection($"Data Source={dbPath}");
            c.Open();

            int projects = QueryCount(c, "projects");
            int snapshots = QueryCount(c, "snapshots");
            int backups = QueryCount(c, "backups");
            string latestBackupUtc = QueryScalar(c, "SELECT MAX(created_utc) FROM backups;");

            return new
            {
                status = "ok",
                dbPathHint = RedactPath(dbPath),
                projects,
                snapshots,
                backups,
                latestBackupUtc
            };
        }
        catch (Exception)
        {
            return new
            {
                status = "error",
                dbPathHint = RedactPath(dbPath),
                projects = 0,
                snapshots = 0,
                backups = 0,
                error = "metadata query failed"
            };
        }
    }

    private static List<object> BuildDestinationMetadataSummary(IEnumerable<BackupDestination>? destinations)
    {
        var output = new List<object>();
        foreach (BackupDestination destination in destinations ?? [])
        {
            if (!destination.Active || string.IsNullOrWhiteSpace(destination.Path))
                continue;

            string root = destination.Path;
            string dbPath = Path.Combine(root, ".vaultsync", "meta", "vaultsync.meta.db");
            if (!File.Exists(dbPath))
            {
                output.Add(new
                {
                    alias = RedactToken(destination.Alias),
                    pathHint = RedactPath(root),
                    status = "missing",
                    projects = 0,
                    snapshots = 0,
                    backups = 0
                });
                continue;
            }

            try
            {
                using var c = new SqliteConnection($"Data Source={dbPath}");
                c.Open();
                output.Add(new
                {
                    alias = RedactToken(destination.Alias),
                    pathHint = RedactPath(root),
                    status = "ok",
                    projects = QueryCount(c, "projects"),
                    snapshots = QueryCount(c, "snapshots"),
                    backups = QueryCount(c, "backups"),
                    latestBackupUtc = QueryScalar(c, "SELECT MAX(created_utc) FROM backups;")
                });
            }
            catch (Exception)
            {
                output.Add(new
                {
                    alias = RedactToken(destination.Alias),
                    pathHint = RedactPath(root),
                    status = "error",
                    projects = 0,
                    snapshots = 0,
                    backups = 0,
                    error = "metadata query failed"
                });
            }
        }

        return output;
    }

    private static int QueryCount(SqliteConnection c, string tableName)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = tableName switch
        {
            "projects" => "SELECT COUNT(1) FROM projects;",
            "snapshots" => "SELECT COUNT(1) FROM snapshots;",
            "backups" => "SELECT COUNT(1) FROM backups;",
            _ => throw new ArgumentException("Unsupported metadata table.", nameof(tableName))
        };
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static string QueryScalar(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static void CopySanitizedDiagnostics(string stagingRoot, AppConfig config)
    {
        CopySanitizedTextFiles(
            GetDiagnosticsDirectory(),
            stagingRoot,
            new SanitizedTextFileSpec(
                [".log", ".txt"],
                DiagnosticsFileLimit,
                DiagnosticsFileByteLimit,
                "diagnostics",
                "diagnostic",
                ".log"),
            config);
    }

    private static void CopySanitizedTelemetry(string stagingRoot, AppConfig config)
    {
        CopySanitizedTextFiles(
            Telemetry.GetTelemetryDirectory(),
            stagingRoot,
            new SanitizedTextFileSpec(
                [TelemetryExtension],
                TelemetryFileLimit,
                TelemetryFileByteLimit,
                "telemetry",
                "events",
                TelemetryExtension),
            config);
    }

    private static void CopySanitizedTextFiles(
        string sourceRoot,
        string stagingRoot,
        SanitizedTextFileSpec spec,
        AppConfig config)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        string targetRoot = Path.Combine(stagingRoot, spec.OutputDirectory);
        Directory.CreateDirectory(targetRoot);
        FileInfo[] sources = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => spec.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(spec.CountLimit)
            .ToArray();

        for (int index = 0; index < sources.Length; index++)
        {
            string content = ReadBoundedText(sources[index].FullName, spec.ByteLimit);
            string sanitized = SanitizeText(content, config);
            string target = Path.Combine(targetRoot, $"{spec.OutputPrefix}-{index + 1:00}{spec.OutputExtension}");
            File.WriteAllText(target, sanitized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed record SanitizedTextFileSpec(
        IReadOnlyCollection<string> Extensions,
        int CountLimit,
        long ByteLimit,
        string OutputDirectory,
        string OutputPrefix,
        string OutputExtension);

    private static string ReadBoundedText(string path, long byteLimit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int requested = (int)Math.Min(stream.Length, byteLimit);
        byte[] buffer = new byte[requested];
        int read = 0;
        while (read < requested)
        {
            int current = stream.Read(buffer, read, requested - read);
            if (current == 0)
                break;
            read += current;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, read);
        return stream.Length > byteLimit
            ? text + Environment.NewLine + "[truncated by VaultSync support-bundle limit]"
            : text;
    }

    internal static string SanitizeText(string text, AppConfig config)
    {
        string sanitized = text ?? string.Empty;
        IEnumerable<string?> exactValues =
        [
            config.ProjectsRoot,
            config.DbPath,
            config.Backups.BackupRoot,
            config.Backups.Location,
            .. config.Backups.Destinations.Select(destination => destination.Path),
            .. config.Backups.Destinations.Select(destination => destination.Alias),
            .. config.Network.Credentials.SelectMany(credential => new[]
            {
                credential.Name,
                credential.Password,
                credential.Username,
                credential.Domain,
                credential.KeyRef
            })
        ];

        foreach (string value in exactValues.Where(value => !string.IsNullOrWhiteSpace(value))!)
            sanitized = sanitized.Replace(value, RedactedValue, StringComparison.OrdinalIgnoreCase);

        sanitized = SensitiveJsonValue.Replace(sanitized, $"$1{RedactedValue}$2");
        sanitized = CredentialReference.Replace(sanitized, "[credential-ref]");
        return UriUserInfo.Replace(sanitized, $"$1{RedactedValue}@");
    }

    private static void WriteManifest(string stagingRoot, DateTimeOffset generatedUtc)
    {
        var entries = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.Ordinal))
            .Select(path =>
            {
                var file = new FileInfo(path);
                string relativePath = Path.GetRelativePath(stagingRoot, path).Replace('\\', '/');
                return new
                {
                    path = relativePath,
                    category = relativePath == ReportFileName
                        ? "report"
                        : relativePath.Split('/', 2)[0],
                    sizeBytes = file.Length,
                    sha256 = ComputeSha256(path)
                };
            })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        object manifest = new
        {
            schemaVersion = 1,
            generatedUtc,
            redaction = "allowlist-v1",
            totalSizeBytes = entries.Sum(entry => entry.sizeBytes),
            entries
        };
        File.WriteAllText(
            Path.Combine(stagingRoot, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetDiagnosticsDirectory()
    {
        return Path.Combine(
            GetFolderSafe(Environment.SpecialFolder.LocalApplicationData),
            "VaultSync",
            "diagnostics");
    }

    internal static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            string trimmed = path.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;

            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.Replace('\\', '/')));
            return $"path-{Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant()}";
        }
        catch
        {
            return RedactedValue;
        }
    }

    private static string RedactToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return RedactedValue;
    }

    private static string GetFolderSafe(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
