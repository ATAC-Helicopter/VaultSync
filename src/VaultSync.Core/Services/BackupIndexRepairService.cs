using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public static class BackupIndexRepairCode
{
    public const string ReassignBackupProjectFromSnapshot = "reassign-backup-project-from-snapshot";
    public const string BackupSnapshotMissing = "backup-snapshot-missing";
    public const string BackupProjectMissing = "backup-project-missing";
    public const string SnapshotProjectMissing = "snapshot-project-missing";
}

public sealed record BackupIndexRepairAction(
    string Code,
    int BackupId,
    int SnapshotId,
    int CurrentProjectId,
    int TargetProjectId,
    string Message,
    IReadOnlyList<string> Evidence);

public sealed record BackupIndexRepairBlockedIssue(
    string Code,
    string Message,
    int Count,
    IReadOnlyList<string> Samples);

public sealed record BackupIndexRepairPlan(
    DateTime GeneratedUtc,
    IReadOnlyList<BackupIndexRepairAction> Actions,
    IReadOnlyList<BackupIndexRepairBlockedIssue> BlockedIssues)
{
    public bool HasActions => Actions.Count > 0;
}

public sealed class BackupIndexRepairService(SqliteRepository repo)
{
    private readonly SqliteRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));

    public BackupIndexRepairPlan BuildPlan()
    {
        DateTime generatedUtc = DateTime.UtcNow;
        var projects = _repo.GetAllProjects().ToList();
        var snapshots = _repo.GetAllSnapshots().ToList();
        List<Backup> backups = _repo.GetAllBackups();

        var projectsById = projects.ToDictionary(static project => project.Id);
        var snapshotsById = snapshots.ToDictionary(static snapshot => snapshot.Id);

        var actions = backups
            .Where(backup => snapshotsById.TryGetValue(backup.SnapshotId, out Snapshot? snapshot) &&
                             snapshot.ProjectId != backup.ProjectId &&
                             projectsById.ContainsKey(snapshot.ProjectId))
            .OrderBy(static backup => backup.CreatedUtc)
            .ThenBy(static backup => backup.Id)
            .Select(backup =>
            {
                Snapshot snapshot = snapshotsById[backup.SnapshotId];
                return new BackupIndexRepairAction(
                    BackupIndexRepairCode.ReassignBackupProjectFromSnapshot,
                    backup.Id,
                    backup.SnapshotId,
                    backup.ProjectId,
                    snapshot.ProjectId,
                    "Repair backup->project link using the authoritative snapshot->project relationship.",
                    [
                        $"backup:{backup.Id}",
                        $"snapshot:{backup.SnapshotId}",
                        $"currentProject:{backup.ProjectId}",
                        $"targetProject:{snapshot.ProjectId}"
                    ]);
            })
            .ToList();

        var blockedIssues = new List<BackupIndexRepairBlockedIssue>();
        AddBlockedIssue(
            blockedIssues,
            BackupIndexRepairCode.BackupSnapshotMissing,
            "Backups reference snapshots that do not exist and cannot be remapped deterministically.",
            [.. backups
                .Where(backup => !snapshotsById.ContainsKey(backup.SnapshotId))
                .Select(backup => $"{backup.Id}:snapshot={backup.SnapshotId}")
                .OrderBy(static sample => sample, StringComparer.Ordinal)]);
        AddBlockedIssue(
            blockedIssues,
            BackupIndexRepairCode.BackupProjectMissing,
            "Backups reference projects that do not exist and require manual repair or import.",
            [.. backups
                .Where(backup => !projectsById.ContainsKey(backup.ProjectId))
                .Select(backup => $"{backup.Id}:project={backup.ProjectId}")
                .OrderBy(static sample => sample, StringComparer.Ordinal)]);
        AddBlockedIssue(
            blockedIssues,
            BackupIndexRepairCode.SnapshotProjectMissing,
            "Snapshots reference projects that do not exist and cannot be reassigned without an exact project match.",
            [.. snapshots
                .Where(snapshot => !projectsById.ContainsKey(snapshot.ProjectId))
                .Select(snapshot => $"{snapshot.Id}:project={snapshot.ProjectId}")
                .OrderBy(static sample => sample, StringComparer.Ordinal)]);

        return new BackupIndexRepairPlan(
            generatedUtc,
            actions,
            blockedIssues);
    }

    public int ApplyPlan(BackupIndexRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        int applied = 0;
        foreach (BackupIndexRepairAction? action in plan.Actions
                     .OrderBy(static action => action.BackupId)
                     .ThenBy(static action => action.TargetProjectId))
        {
            if (!string.Equals(action.Code, BackupIndexRepairCode.ReassignBackupProjectFromSnapshot, StringComparison.Ordinal))
                continue;

            _repo.UpdateBackupProjectId(action.BackupId, action.TargetProjectId);
            applied++;
        }

        return applied;
    }

    private static void AddBlockedIssue(
        List<BackupIndexRepairBlockedIssue> blockedIssues,
        string code,
        string message,
        IReadOnlyList<string> samples)
    {
        if (samples.Count == 0)
            return;

        blockedIssues.Add(new BackupIndexRepairBlockedIssue(
            code,
            message,
            samples.Count,
            [.. samples.Take(5)]));
    }
}
