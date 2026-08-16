using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

public sealed record SupportBundleExportResult(bool Success, string? ZipPath, string Message);

public sealed class SupportBundleService
{
    private const string RedactedValue = "[redacted]";

    public static SupportBundleExportResult Export(IAppConfigStore? configStore = null)
    {
        configStore ??= StaticAppConfigStore.Instance;
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

            string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            File.WriteAllText(Path.Combine(stagingRoot, "support-report.json"), reportJson);

            TryCopyDiagnostics(stagingRoot);
            TryExportTelemetry(stagingRoot);

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

    private static object BuildBundleReport(AppConfig config, DateTimeOffset timestamp)
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

    private static object BuildRedactedConfig(AppConfig config)
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
                    config.Backups.Encryption.KdfParamRef,
                    config.Backups.Encryption.AllowSessionFallback,
                    config.Backups.Encryption.OpenUnlockTimeoutMinutes
                },
                destinations = config.Backups.Destinations.Select(d => new
                {
                    d.Alias,
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
                    c.Name,
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
                config.Appearance.TagColors
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
                    config.Advanced.UpdateDiagnostics.Error,
                    patchPreflight = new
                    {
                        config.Advanced.UpdateDiagnostics.PatchPreflight.StatusCode,
                        config.Advanced.UpdateDiagnostics.PatchPreflight.Message,
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
                    config.Advanced.MetadataConflictTelemetry.LastResolvedProject,
                    conflicts = (config.Advanced.ProjectMetadataConflicts ?? [])
                        .Select(conflict => new
                        {
                            conflict.ProjectExternalId,
                            conflict.ProjectName,
                            conflict.SourceMachineId,
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
                    config.Advanced.CheckpointResumeTelemetry.LastProjectName,
                    config.Advanced.CheckpointResumeTelemetry.LastBackupFolder,
                    config.Advanced.CheckpointResumeTelemetry.LastArchivePath,
                    config.Advanced.CheckpointResumeTelemetry.LastResumeOffsetBytes,
                    config.Advanced.CheckpointResumeTelemetry.LastArchiveSizeBytes,
                    config.Advanced.CheckpointResumeTelemetry.LastSourceFingerprint,
                    config.Advanced.CheckpointResumeTelemetry.LastMessage
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
        catch (Exception ex)
        {
            return new
            {
                status = "error",
                dbPathHint = RedactPath(dbPath),
                projects = 0,
                snapshots = 0,
                backups = 0,
                error = ex.Message
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
                    alias = destination.Alias,
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
                    alias = destination.Alias,
                    pathHint = RedactPath(root),
                    status = "ok",
                    projects = QueryCount(c, "projects"),
                    snapshots = QueryCount(c, "snapshots"),
                    backups = QueryCount(c, "backups"),
                    latestBackupUtc = QueryScalar(c, "SELECT MAX(created_utc) FROM backups;")
                });
            }
            catch (Exception ex)
            {
                output.Add(new
                {
                    alias = destination.Alias,
                    pathHint = RedactPath(root),
                    status = "error",
                    projects = 0,
                    snapshots = 0,
                    backups = 0,
                    error = ex.Message
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

    private static void TryCopyDiagnostics(string stagingRoot)
    {
        try
        {
            string diagnosticsRoot = Path.Combine(
                GetFolderSafe(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "diagnostics");

            if (!Directory.Exists(diagnosticsRoot))
                return;

            string outRoot = Path.Combine(stagingRoot, "diagnostics");
            Directory.CreateDirectory(outRoot);

            var files = Directory
                .EnumerateFiles(diagnosticsRoot)
                .Where(path =>
                {
                    string ext = Path.GetExtension(path);
                    return ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => new FileInfo(path))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(12)
                .ToList();

            foreach (FileInfo? file in files)
            {
                string target = Path.Combine(outRoot, file.Name);
                File.Copy(file.FullName, target, overwrite: true);
            }
        }
        catch
        {
            // Best effort diagnostics copy.
        }
    }

    private static void TryExportTelemetry(string stagingRoot)
    {
        try
        {
            string telemetryOut = Path.Combine(stagingRoot, "telemetry");
            Directory.CreateDirectory(telemetryOut);
            Telemetry.ExportToZip(telemetryOut);
        }
        catch
        {
            // Best effort telemetry export.
        }
    }

    private static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            string trimmed = path.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;

            string leaf = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(leaf))
                return RedactedValue;

            return $"...{Path.DirectorySeparatorChar}{leaf}";
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
