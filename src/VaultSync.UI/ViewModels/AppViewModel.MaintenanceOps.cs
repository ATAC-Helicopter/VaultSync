using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private static readonly TimeSpan MaintenanceCheckInterval = TimeSpan.FromMinutes(5);

        private void ConfigureMaintenanceTimer()
        {
            _maintenanceTimer?.Dispose();
            _maintenanceTimer = null;

            if (!_config.Advanced.Maintenance.Enabled)
                return;

            _maintenanceTimer = new Timer(
                _ => AppViewModel.RunDetached(SafeRunScheduledMaintenanceAsync, nameof(SafeRunScheduledMaintenanceAsync)),
                null,
                TimeSpan.FromMinutes(1),
                MaintenanceCheckInterval);
        }

        private async Task SafeRunScheduledMaintenanceAsync()
        {
            try
            {
                await RunScheduledMaintenanceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record(
                    $"Scheduled maintenance failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task RunScheduledMaintenanceAsync()
        {
            if (Interlocked.Exchange(ref _maintenanceInFlight, 1) == 1)
                return;

            try
            {
                AppConfig cfg = _configStore.Load();
                MaintenanceConfig maintenance = cfg.Advanced.Maintenance ??= new MaintenanceConfig();
                if (!maintenance.Enabled)
                    return;

                DateTimeOffset nowLocal = DateTimeOffset.Now;
                QuietHoursDecision window = QuietHoursPolicy.Evaluate(true, maintenance.WindowStart, maintenance.WindowEnd, nowLocal);
                if (!window.IsInQuietHours)
                    return;

                if (HasMaintenanceRunToday(maintenance.LastRunUtc, nowLocal))
                    return;

                MaintenanceRunOutcome outcome = await ExecuteMaintenanceRunAsync(cfg).ConfigureAwait(false);
                cfg.Advanced.Maintenance.LastRunUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                cfg.Advanced.Maintenance.LastStatus = outcome.Status;
                _configStore.Save(cfg);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _config.Advanced.Maintenance.LastRunUtc = cfg.Advanced.Maintenance.LastRunUtc;
                    _config.Advanced.Maintenance.LastStatus = cfg.Advanced.Maintenance.LastStatus;
                });

                DiagnosticsLogger.Record(
                    $"Scheduled maintenance complete: {outcome.Status}");
            }
            finally
            {
                Interlocked.Exchange(ref _maintenanceInFlight, 0);
            }
        }

        private async Task<MaintenanceRunOutcome> ExecuteMaintenanceRunAsync(AppConfig cfg)
        {
            var parts = new System.Collections.Generic.List<string>();

            if (cfg.Advanced.Maintenance.RunConsistencyScan)
            {
                BackupIndexConsistencyReport report = await Task.Run(() => _backupIndexConsistencyService.Scan()).ConfigureAwait(false);
                BackupIndexConsistencySummary summarySnapshot = BackupIndexConsistencyService.BuildSummary(report);
                await Task.Run(() => PersistBackupIndexConsistencySummary(summarySnapshot)).ConfigureAwait(false);
                string summary = BuildBackupIndexConsistencyStatus(report);

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

                parts.Add($"consistency={report.ErrorCount}e/{report.WarningCount}w");
            }

            if (cfg.Advanced.Maintenance.RunRepairDryRun)
            {
                BackupIndexRepairPlan plan = await Task.Run(() =>
                {
                    var service = new BackupIndexRepairService(_repo);
                    return service.BuildPlan();
                }).ConfigureAwait(false);

                parts.Add($"repair={plan.Actions.Count}a/{plan.BlockedIssues.Count}b");
            }

            if (cfg.Advanced.Maintenance.RunMetadataRefresh)
            {
                int queuedCount = 0;

                if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot) &&
                    cfg.Backups.EnableMetadataSync &&
                    cfg.Backups.AutoImportMetadata)
                {
                    TryImportMetadataFromRoot(cfg.ProjectsRoot);
                    queuedCount++;
                }

                foreach (BackupDestination dest in AppViewModel.GetActiveDestinations(cfg))
                {
                    if (!IsMetadataImportEnabled(cfg, dest))
                        continue;

                    NetworkCredentialProfile? profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    DestinationResolution resolution = await Task.Run(() => _networkMountService.PrepareDestination(dest, profile)).ConfigureAwait(false);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    TryImportMetadataForDestination(cfg, dest, resolution.EffectivePath);
                    queuedCount++;
                }

                parts.Add($"metadata={queuedCount}q");
            }

            if (parts.Count == 0)
                parts.Add("no-op");

            return new MaintenanceRunOutcome(string.Join("; ", parts));
        }

        private static bool HasMaintenanceRunToday(string? lastRunUtc, DateTimeOffset nowLocal)
        {
            if (!DateTimeOffset.TryParse(lastRunUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsedUtc))
                return false;

            return parsedUtc.ToLocalTime().Date == nowLocal.Date;
        }

        private readonly record struct MaintenanceRunOutcome(string Status);
    }
}
