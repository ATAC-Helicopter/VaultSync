using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private bool ShouldRunVerification(Project project, bool isAutoRun, bool globalVerifyAfterCreate)
        {
            var policy = ProjectVerificationPolicy.Normalize(project.VerificationPolicy);
            return policy switch
            {
                ProjectVerificationPolicy.Always => true,
                ProjectVerificationPolicy.Scheduled => isAutoRun,
                ProjectVerificationPolicy.Manual => false,
                _ => globalVerifyAfterCreate
            };
        }

        private void StartPostBackupHashingAsync(Project project, int snapshotId)
        {
            _ = Task.Run(async () =>
            {
                Console.WriteLine($"[Backup] Post-hash start: project='{project.Name}', snapshotId={snapshotId}");
                try
                {
                    var snapshotService = new SnapshotService(_repo, new HashService());
                    var hashed = await snapshotService.HashMissingFilesAsync(project, snapshotId, CancellationToken.None);
                    Telemetry.Log("backup_post_hash_complete", b => b
                        .WithHashedString("project", project.Name)
                        .WithCount("hashedFiles", hashed));
                    Console.WriteLine($"[Backup] Post-hash complete: project='{project.Name}', hashedFiles={hashed}");
                }
                catch (Exception ex)
                {
                    Telemetry.Log("backup_post_hash_failed", b => b
                        .WithHashedString("project", project.Name)
                        .WithException(ex));
                    Console.WriteLine($"[Backup] Post-hash failed: project='{project.Name}', error={ex.Message}");
                }
            });
        }

        private void RecordBackupThroughput(int backupId, TimeSpan elapsed, bool useArchiveMode)
        {
            try
            {
                if (backupId <= 0)
                    return;

                if (elapsed.TotalSeconds <= 1)
                    return;

                var backup = _repo.GetBackupById(backupId);
                if (backup is null || backup.TotalBytes <= 0)
                    return;

                var mbSec = backup.TotalBytes / (1024d * 1024d) / elapsed.TotalSeconds;
                if (double.IsNaN(mbSec) || double.IsInfinity(mbSec) || mbSec <= 0)
                    return;

                _ = Task.Run(() =>
                {
                    try
                    {
                        var cfg = AppConfigStore.Load();
                        var existing = useArchiveMode
                            ? cfg.Backups.LastBackupThroughputArchiveMbSec
                            : cfg.Backups.LastBackupThroughputCopyMbSec;
                        var blended = existing > 0 ? (existing * 0.7 + mbSec * 0.3) : mbSec;
                        var rounded = Math.Round(blended, 2);
                        if (useArchiveMode)
                        {
                            cfg.Backups.LastBackupThroughputArchiveMbSec = rounded;
                        }
                        else
                        {
                            cfg.Backups.LastBackupThroughputCopyMbSec = rounded;
                        }
                        cfg.Backups.LastBackupThroughputMbSec = rounded;
                        AppConfigStore.Save(cfg);

                        Dispatcher.UIThread.Post(() =>
                        {
                            _config.Backups.LastBackupThroughputArchiveMbSec = cfg.Backups.LastBackupThroughputArchiveMbSec;
                            _config.Backups.LastBackupThroughputCopyMbSec = cfg.Backups.LastBackupThroughputCopyMbSec;
                            _config.Backups.LastBackupThroughputMbSec = cfg.Backups.LastBackupThroughputMbSec;
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Backup] Failed to persist throughput: {ex.Message}");
                    }
                });
            }
            catch
            {
                // best-effort only; ignore throughput persistence errors
            }
        }

        private void StartVerificationAsync(Project project, Backup latest, string backupRoot, string telemetryEvent)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[Backup] Verification start: project='{project.Name}', backupId={latest.Id}, snapshotId={latest.SnapshotId}");
                    var snapshotService = new SnapshotService(_repo, new HashService());
                    await snapshotService.HashMissingFilesAsync(project, latest.SnapshotId, CancellationToken.None);

                    var verifyService = new VerifyService(_repo, new HashService());
                    var folder = Path.Combine(backupRoot, latest.Path ?? string.Empty);
                    await verifyService.VerifyAsync(project, folder, 100, full: true);
                    Console.WriteLine($"[Backup] Verification complete: project='{project.Name}', backupId={latest.Id}");
                }
                catch (Exception vex)
                {
                    Telemetry.Log(telemetryEvent, b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithHashedString("destinationPath", backupRoot)
                        .WithException(vex));
                    Console.WriteLine($"[Backup] Verification failed: project='{project.Name}', backupId={latest.Id}, error={vex.Message}");

                    if (NotificationsEnabled)
                    {
                        var title = L("Backups.Verification.Title", "Backup verification failed");
                        var msg = Lf("Backups.Verification.FailureMessage", "Verification failed for '{0}'. The backup may be corrupted or incomplete.", project.Name);

                        _notificationService.ShowError(title, msg, NotificationKind.Backup);

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        var backupId = latest.Id.ToString();
                        BackupsViewModel.MarkSnapshotAsFailed(backupId);
                        BackupsViewModel.ShowVerificationFailure(backupId, project.Name);
                    });
                }
            });
        }

        private void TryDeleteSnapshotIfOrphan(int projectId, int snapshotId)
        {
            try
            {
                // If any other backup references this snapshot, keep it.
                var remaining = _repo.HasBackupForSnapshot(projectId, snapshotId);
                if (remaining)
                    return;

                var project = _repo.GetProjectById(projectId);
                if (project is null)
                    return;

                _repo.DeleteSnapshotsById(project.Name, new[] { snapshotId });
            }
            catch
            {
                // Ignore snapshot cleanup failures for now.
            }
        }

        private void OnBackupProtectionChanged(int backupId, bool isProtected)
        {
            try
            {
                _repo.SetBackupProtection(backupId, isProtected);
                UpdateBackupProtectionMarker(backupId, isProtected);
            }
            catch
            {
                // swallow for now; could surface notification later
            }
        }

        private sealed record DriveHealthDecision(bool Block, string Message, NotificationSeverity Severity);

        private async Task<DriveHealthDecision> EvaluateDriveHealthAsync(string projectPath, string backupPath)
        {
            return await Task.Run(() =>
            {
                var block = ShouldBlockForDriveHealth(projectPath, backupPath, out var msg, out var sev);
                return new DriveHealthDecision(block, msg, sev);
            });
        }

        private bool ShouldBlockForDriveHealth(string projectPath, string backupPath, out string message, out NotificationSeverity severity)
        {
            message  = string.Empty;
            severity = NotificationSeverity.Warning;

            if (_settingsViewModel?.ShowDriveHealthWarnings != true)
                return false;

            var results = new List<DriveHealthResult>
            {
                _driveHealthService.CheckPath(projectPath),
                _driveHealthService.CheckPath(backupPath)
            };

            DriveHealthResult? issue = null;
            foreach (var r in results)
            {
                if (r.Status == DriveHealthStatus.Failing)
                {
                    issue = r;
                    break;
                }

                if (r.Status == DriveHealthStatus.Warning && issue is null)
                {
                    issue = r;
                }
            }

            if (issue is null || issue.Status == DriveHealthStatus.Unknown || issue.Status == DriveHealthStatus.Healthy)
                return false;

            var driveLabel = issue.DriveId ?? issue.Path ?? L("DriveHealth.UnknownDrive", "drive");
            severity = issue.Status == DriveHealthStatus.Failing
                ? NotificationSeverity.Error
                : NotificationSeverity.Warning;

            message = issue.Status == DriveHealthStatus.Failing
                ? Lf("DriveHealth.BlockedMessage", "Backup skipped: drive health failing on {0} ({1}).", driveLabel, issue.Message)
                : Lf("DriveHealth.WarningMessage", "Drive health warning on {0}: {1}.", driveLabel, issue.Message);

            return issue.Status == DriveHealthStatus.Failing;
        }

        private void ShowDriveHealthNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (!NotificationsEnabled)
                return;

            var title = severity == NotificationSeverity.Error
                ? L("Backups.Notification.DriveBlockedTitle", "Backup blocked: drive health")
                : L("Backups.Notification.DriveWarningTitle", "Drive health warning");

            GlobalNotificationCenter.Instance.Show(
                message,
                severity,
                title);

            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    severity,
                    title);
            }
        }

        /// <summary>
        /// Returns true when backups should be paused because the device is on battery and the user enabled the setting.
        /// </summary>
    }
}
