using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public sealed class BackupRetentionSimulationService(SqliteRepository repo)
{
    private readonly SqliteRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));

    public BackupRetentionSimulationResult Simulate(int maxSnapshotsPerProject)
    {
        int normalizedMaxSnapshots = Math.Max(1, maxSnapshotsPerProject);
        var projects = _repo.GetAllProjects().ToDictionary(project => project.Id);
        var backups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)
            .OrderByDescending(backup => backup.CreatedUtc)
            .ThenByDescending(backup => backup.Id)
            .ToList();
        var snapshotsById = _repo.GetAllSnapshots().ToDictionary(snapshot => snapshot.Id);
        var metadataBySnapshotId = _repo.GetSnapshotHistoryMetadataBySnapshotIds(backups.Select(backup => backup.SnapshotId));
        var byteVerifiedBackupIds = _repo.GetRecoveryDrills()
            .GroupBy(drill => drill.BackupId)
            .Select(group => group.OrderByDescending(drill => drill.RunUtc).ThenByDescending(drill => drill.Id).First())
            .Where(RecoveryDrillService.HasPassedByteIntegrity)
            .Select(drill => drill.BackupId)
            .ToHashSet();

        var projectResults = new List<ProjectRetentionSimulationProjectResult>();

        foreach (Project? project in projects.Values.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            var projectBackups = backups
                .Where(backup => backup.ProjectId == project.Id)
                .OrderByDescending(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.Id)
                .ToList();
            var unprotected = projectBackups
                .Where(backup =>
                    !backup.IsProtected &&
                    (!metadataBySnapshotId.TryGetValue(backup.SnapshotId, out SnapshotHistoryMetadata? metadata) || !metadata.IsProtected))
                .ToList();
            int deleteQuota = Math.Max(0, unprotected.Count - normalizedMaxSnapshots);
            var candidates = unprotected
                .OrderBy(backup => backup.CreatedUtc)
                .ThenBy(backup => backup.Id)
                .ToList();
            var projectSnapshots = snapshotsById
                .Where(entry => entry.Value.ProjectId == project.Id)
                .ToDictionary(entry => entry.Key, entry => entry.Value);

            BackupService.BackupRetentionPreflightResult preflight = BackupService.EvaluateRetentionPreflight(
                project.Id,
                projectBackups,
                candidates,
                projectSnapshots,
                deleteQuota);
            IReadOnlyList<BackupService.BackupRetentionCandidateDecision> decisions = BackupService.BuildRetentionDeletionPlan(
                project.Id,
                projectBackups,
                candidates,
                projectSnapshots,
                deleteQuota,
                byteVerifiedBackupIds);
            var selectedIds = decisions
                .Where(static decision => decision.Selected)
                .Select(static decision => decision.BackupId)
                .ToHashSet();
            var selectedBackups = projectBackups
                .Where(backup => selectedIds.Contains(backup.Id))
                .OrderBy(backup => backup.CreatedUtc)
                .ThenBy(backup => backup.Id)
                .ToList();
            int skippedUnsafeCount = decisions.Count(decision =>
                !decision.Selected &&
                !string.Equals(decision.Code, "quota-satisfied", StringComparison.OrdinalIgnoreCase));

            projectResults.Add(new ProjectRetentionSimulationProjectResult(
                project.Id,
                string.IsNullOrWhiteSpace(project.Name) ? $"Project {project.Id}" : project.Name.Trim(),
                projectBackups.Count,
                unprotected.Count,
                preflight.ValidRestorePointCount,
                deleteQuota,
                preflight.CanPrune,
                preflight.Code,
                preflight.Message,
                selectedBackups.Count,
                selectedBackups.Sum(static backup => backup.TotalBytes),
                skippedUnsafeCount));
        }

        var affectedProjects = projectResults.Where(result => result.DeleteQuota > 0 || !result.CanPrune).ToList();
        return new BackupRetentionSimulationResult(
            normalizedMaxSnapshots,
            projectResults.Count,
            affectedProjects.Count,
            affectedProjects.Count(result => !result.CanPrune),
            affectedProjects.Sum(result => result.SelectedDeleteCount),
            affectedProjects.Sum(result => result.SelectedDeleteBytes),
            affectedProjects);
    }
}

public sealed record BackupRetentionSimulationResult(
    int MaxSnapshotsPerProject,
    int TotalProjectCount,
    int AffectedProjectCount,
    int BlockedProjectCount,
    int SuggestedDeleteCount,
    long SuggestedDeleteBytes,
    IReadOnlyList<ProjectRetentionSimulationProjectResult> Projects);

public sealed record ProjectRetentionSimulationProjectResult(
    int ProjectId,
    string ProjectName,
    int BackupCount,
    int UnprotectedBackupCount,
    int ValidRestorePointCount,
    int DeleteQuota,
    bool CanPrune,
    string PreflightCode,
    string PreflightMessage,
    int SelectedDeleteCount,
    long SelectedDeleteBytes,
    int SkippedUnsafeCount)
{
    public string Summary =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: keep={1}, delete={2}, reclaim={3} B, blocked={4}",
            ProjectName,
            BackupCount - SelectedDeleteCount,
            SelectedDeleteCount,
            SelectedDeleteBytes,
            !CanPrune);
}
