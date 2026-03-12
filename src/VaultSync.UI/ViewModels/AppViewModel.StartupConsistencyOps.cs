using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private async Task RunStartupBackupIndexConsistencyCheckAsync()
        {
            var report = await Task.Run(() => _backupIndexConsistencyService.Scan()).ConfigureAwait(false);

            var summary = BuildBackupIndexConsistencyStatus(report);
            DiagnosticsLogger.Record(
                $"Backup index consistency scan complete: projects={report.ProjectCount}, snapshots={report.SnapshotCount}, backups={report.BackupCount}, warnings={report.WarningCount}, errors={report.ErrorCount}.");

            if (report.HasIssues)
            {
                foreach (var finding in report.Findings.Take(10))
                {
                    DiagnosticsLogger.Record(
                        $"Backup index finding [{finding.Severity}] {finding.Code}: count={finding.Count}; samples={string.Join(" | ", finding.Samples)}");
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _backupIndexConsistencyReport = report;
                BackupIndexConsistencyStatus = summary;
                OnPropertyChanged(nameof(BackupIndexConsistencyReport));
                OnPropertyChanged(nameof(HasBackupIndexConsistencyIssues));
            });
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
