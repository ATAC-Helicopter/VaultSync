using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private async Task RunStartupBackupIndexConsistencyCheckAsync()
        {
            BackupIndexConsistencyReport report = await Task.Run(() => _backupIndexConsistencyService.Scan()).ConfigureAwait(false);
            BackupIndexConsistencySummary summarySnapshot = BackupIndexConsistencyService.BuildSummary(report);
            await Task.Run(() => PersistBackupIndexConsistencySummary(summarySnapshot)).ConfigureAwait(false);

            string summary = BuildBackupIndexConsistencyStatus(report);
            DiagnosticsLogger.Record(
                $"Backup index consistency scan complete: projects={report.ProjectCount}, snapshots={report.SnapshotCount}, backups={report.BackupCount}, warnings={report.WarningCount}, errors={report.ErrorCount}.");

            if (report.HasIssues)
            {
                foreach (BackupIndexConsistencyFinding? finding in report.Findings.Take(10))
                {
                    DiagnosticsLogger.Record(
                        $"Backup index finding [{finding.Severity}] {finding.Code}: count={finding.Count}; samples={string.Join(" | ", finding.Samples)}");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _backupIndexConsistencyReport = report;
                _config.Advanced.BackupIndexLastScan = new BackupIndexScanSummary
                {
                    CheckedUtc = summarySnapshot.CheckedUtc,
                    ProjectCount = summarySnapshot.ProjectCount,
                    SnapshotCount = summarySnapshot.SnapshotCount,
                    BackupCount = summarySnapshot.BackupCount,
                    ErrorCount = summarySnapshot.ErrorCount,
                    WarningCount = summarySnapshot.WarningCount,
                    TopFindingCodes = [.. summarySnapshot.TopFindingCodes]
                };
                BackupIndexConsistencyStatus = summary;
                OnPropertyChanged(nameof(BackupIndexConsistencyReport));
                OnPropertyChanged(nameof(HasBackupIndexConsistencyIssues));
            });
        }

        private static void PersistBackupIndexConsistencySummary(BackupIndexConsistencySummary summary)
        {
            try
            {
                AppConfig config = AppConfigStore.Load();
                config.Advanced.BackupIndexLastScan = new BackupIndexScanSummary
                {
                    CheckedUtc = summary.CheckedUtc,
                    ProjectCount = summary.ProjectCount,
                    SnapshotCount = summary.SnapshotCount,
                    BackupCount = summary.BackupCount,
                    ErrorCount = summary.ErrorCount,
                    WarningCount = summary.WarningCount,
                    TopFindingCodes = [.. summary.TopFindingCodes]
                };
                AppConfigStore.Save(config);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Backup index summary persist failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private string BuildBackupIndexConsistencyStatus(BackupIndexConsistencyReport report)
        {
            if (!report.HasIssues)
            {
                return L("Diagnostics.BackupIndex.Healthy", "Backup index healthy.");
            }

            if (report.ErrorCount > 0)
            {
                return Lf(
                    "Diagnostics.BackupIndex.Errors",
                    "Backup index found {0} errors and {1} warnings.",
                    report.ErrorCount,
                    report.WarningCount);
            }

            return Lf(
                "Diagnostics.BackupIndex.Warnings",
                "Backup index found {0} warnings.",
                report.WarningCount);
        }
    }
}
