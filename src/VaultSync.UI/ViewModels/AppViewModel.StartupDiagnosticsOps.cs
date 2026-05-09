using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void RecordStartupPhase(string phaseName)
        {
            if (string.IsNullOrWhiteSpace(phaseName))
            {
                return;
            }

            long elapsedMs = _startupDiagnosticsStopwatch.ElapsedMilliseconds;
            lock (_startupDiagnosticsGate)
            {
                _startupDiagnosticsPhases.Add(new StartupDiagnosticsPhase
                {
                    Name = phaseName,
                    ElapsedMs = elapsedMs
                });
            }
        }

        private void PersistStartupDiagnosticsSummary()
        {
            try
            {
                StartupDiagnosticsPhase[] phases;
                lock (_startupDiagnosticsGate)
                {
                    phases = [.. _startupDiagnosticsPhases
                        .OrderBy(phase => phase.ElapsedMs)
                        .Select(phase => new StartupDiagnosticsPhase
                        {
                            Name = phase.Name,
                            ElapsedMs = phase.ElapsedMs
                        })];
                }

                var summary = new StartupDiagnosticsSummary
                {
                    LastCompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    TotalDurationMs = _startupDiagnosticsStopwatch.ElapsedMilliseconds,
                    Phases = [.. phases]
                };

                AppConfig cfg = AppConfigStore.Load();
                cfg.Advanced.StartupDiagnostics = summary;
                AppConfigStore.Save(cfg);

                _config.Advanced.StartupDiagnostics = new StartupDiagnosticsSummary
                {
                    LastCompletedUtc = summary.LastCompletedUtc,
                    TotalDurationMs = summary.TotalDurationMs,
                    Phases = [.. summary.Phases
                        .Select(phase => new StartupDiagnosticsPhase
                        {
                            Name = phase.Name,
                            ElapsedMs = phase.ElapsedMs
                        })]
                };
                Dispatcher.UIThread.Post(() => _settingsViewModel.ReloadStartupDiagnostics());

                DiagnosticsLogger.Record(
                    $"Startup diagnostics timeline persisted: total={summary.TotalDurationMs}ms; phases={string.Join(", ", summary.Phases.Select(phase => $"{phase.Name}={phase.ElapsedMs}ms"))}.");
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup diagnostics persist failed: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }
}
