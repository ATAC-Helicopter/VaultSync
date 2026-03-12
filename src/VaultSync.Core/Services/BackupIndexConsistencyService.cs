using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public enum BackupIndexConsistencySeverity
{
    Info,
    Warning,
    Error
}

public static class BackupIndexConsistencyCode
{
    public const string MissingProjectExternalId = "missing-project-external-id";
    public const string MissingSnapshotExternalId = "missing-snapshot-external-id";
    public const string MissingBackupExternalId = "missing-backup-external-id";
    public const string DuplicateProjectExternalId = "duplicate-project-external-id";
    public const string DuplicateSnapshotExternalId = "duplicate-snapshot-external-id";
    public const string DuplicateBackupExternalId = "duplicate-backup-external-id";
    public const string SnapshotProjectMissing = "snapshot-project-missing";
    public const string BackupProjectMissing = "backup-project-missing";
    public const string BackupSnapshotMissing = "backup-snapshot-missing";
    public const string BackupSnapshotProjectMismatch = "backup-snapshot-project-mismatch";
}

public sealed record BackupIndexConsistencyFinding(
    string Code,
    BackupIndexConsistencySeverity Severity,
    string Message,
    int Count,
    IReadOnlyList<string> Samples);

public sealed record BackupIndexConsistencyReport(
    DateTime CheckedUtc,
    int ProjectCount,
    int SnapshotCount,
    int BackupCount,
    IReadOnlyList<BackupIndexConsistencyFinding> Findings)
{
    public int ErrorCount => Findings.Count(f => f.Severity == BackupIndexConsistencySeverity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == BackupIndexConsistencySeverity.Warning);
    public bool HasIssues => Findings.Count > 0;
}

public sealed record BackupIndexConsistencySummary(
    string CheckedUtc,
    int ProjectCount,
    int SnapshotCount,
    int BackupCount,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<string> TopFindingCodes);

public sealed class BackupIndexConsistencyService
{
    private readonly SqliteRepository _repo;

    public BackupIndexConsistencyService(SqliteRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public BackupIndexConsistencyReport Scan()
    {
        var checkedUtc = DateTime.UtcNow;
        var projects = _repo.GetAllProjects().ToList();
        var snapshots = _repo.GetAllSnapshots().ToList();
        var backups = _repo.GetAllBackups();

        var findings = new List<BackupIndexConsistencyFinding>();

        AppendMissingExternalIdFindings(
            findings,
            projects,
            p => p.ExternalId,
            BackupIndexConsistencyCode.MissingProjectExternalId,
            "Projects are missing external IDs required for metadata sync.",
            p => $"{p.Id}:{p.Name}");
        AppendMissingExternalIdFindings(
            findings,
            snapshots,
            s => s.ExternalId,
            BackupIndexConsistencyCode.MissingSnapshotExternalId,
            "Snapshots are missing external IDs required for metadata sync.",
            s => $"{s.Id}:project={s.ProjectId}");
        AppendMissingExternalIdFindings(
            findings,
            backups,
            b => b.ExternalId,
            BackupIndexConsistencyCode.MissingBackupExternalId,
            "Backups are missing external IDs required for metadata sync.",
            b => $"{b.Id}:project={b.ProjectId}:snapshot={b.SnapshotId}");

        AppendDuplicateExternalIdFindings(
            findings,
            projects,
            p => p.ExternalId,
            BackupIndexConsistencyCode.DuplicateProjectExternalId,
            "Projects share duplicate external IDs.",
            p => $"{p.Id}:{p.Name}");
        AppendDuplicateExternalIdFindings(
            findings,
            snapshots,
            s => s.ExternalId,
            BackupIndexConsistencyCode.DuplicateSnapshotExternalId,
            "Snapshots share duplicate external IDs.",
            s => $"{s.Id}:project={s.ProjectId}");
        AppendDuplicateExternalIdFindings(
            findings,
            backups,
            b => b.ExternalId,
            BackupIndexConsistencyCode.DuplicateBackupExternalId,
            "Backups share duplicate external IDs.",
            b => $"{b.Id}:project={b.ProjectId}:snapshot={b.SnapshotId}");

        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var snapshotsById = snapshots.ToDictionary(s => s.Id);

        var snapshotsWithMissingProject = snapshots
            .Where(s => !projectIds.Contains(s.ProjectId))
            .Select(s => $"{s.Id}:project={s.ProjectId}")
            .OrderBy(static sample => sample, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (snapshotsWithMissingProject.Count > 0)
        {
            findings.Add(new BackupIndexConsistencyFinding(
                BackupIndexConsistencyCode.SnapshotProjectMissing,
                BackupIndexConsistencySeverity.Error,
                "Snapshots reference projects that do not exist.",
                snapshots.Count(s => !projectIds.Contains(s.ProjectId)),
                snapshotsWithMissingProject));
        }

        var backupsWithMissingProject = backups
            .Where(b => !projectIds.Contains(b.ProjectId))
            .Select(b => $"{b.Id}:project={b.ProjectId}")
            .OrderBy(static sample => sample, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (backupsWithMissingProject.Count > 0)
        {
            findings.Add(new BackupIndexConsistencyFinding(
                BackupIndexConsistencyCode.BackupProjectMissing,
                BackupIndexConsistencySeverity.Error,
                "Backups reference projects that do not exist.",
                backups.Count(b => !projectIds.Contains(b.ProjectId)),
                backupsWithMissingProject));
        }

        var backupsWithMissingSnapshot = backups
            .Where(b => !snapshotsById.ContainsKey(b.SnapshotId))
            .Select(b => $"{b.Id}:snapshot={b.SnapshotId}")
            .OrderBy(static sample => sample, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (backupsWithMissingSnapshot.Count > 0)
        {
            findings.Add(new BackupIndexConsistencyFinding(
                BackupIndexConsistencyCode.BackupSnapshotMissing,
                BackupIndexConsistencySeverity.Error,
                "Backups reference snapshots that do not exist.",
                backups.Count(b => !snapshotsById.ContainsKey(b.SnapshotId)),
                backupsWithMissingSnapshot));
        }

        var mismatchedBackups = backups
            .Where(b => snapshotsById.TryGetValue(b.SnapshotId, out var snapshot) && snapshot.ProjectId != b.ProjectId)
            .Select(b =>
            {
                var snapshot = snapshotsById[b.SnapshotId];
                return $"{b.Id}:project={b.ProjectId}:snapshot={b.SnapshotId}:snapshotProject={snapshot.ProjectId}";
            })
            .OrderBy(static sample => sample, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (mismatchedBackups.Count > 0)
        {
            findings.Add(new BackupIndexConsistencyFinding(
                BackupIndexConsistencyCode.BackupSnapshotProjectMismatch,
                BackupIndexConsistencySeverity.Warning,
                "Backups point to snapshots owned by a different project.",
                backups.Count(b => snapshotsById.TryGetValue(b.SnapshotId, out var snapshot) && snapshot.ProjectId != b.ProjectId),
                mismatchedBackups));
        }

        return new BackupIndexConsistencyReport(
            checkedUtc,
            projects.Count,
            snapshots.Count,
            backups.Count,
            findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Code, StringComparer.Ordinal)
                .ToList());
    }

    public static BackupIndexConsistencySummary BuildSummary(BackupIndexConsistencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new BackupIndexConsistencySummary(
            report.CheckedUtc.ToString("O"),
            report.ProjectCount,
            report.SnapshotCount,
            report.BackupCount,
            report.ErrorCount,
            report.WarningCount,
            report.Findings
                .OrderByDescending(static finding => finding.Count)
                .ThenByDescending(static finding => finding.Severity)
                .ThenBy(static finding => finding.Code, StringComparer.Ordinal)
                .Take(5)
                .Select(static finding => finding.Code)
                .ToList());
    }

    private static void AppendMissingExternalIdFindings<T>(
        ICollection<BackupIndexConsistencyFinding> findings,
        IReadOnlyCollection<T> items,
        Func<T, string?> externalIdSelector,
        string code,
        string message,
        Func<T, string> sampleFormatter)
    {
        var missing = items
            .Where(item => string.IsNullOrWhiteSpace(externalIdSelector(item)))
            .Select(sampleFormatter)
            .OrderBy(static sample => sample, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (missing.Count == 0)
            return;

        findings.Add(new BackupIndexConsistencyFinding(
            code,
            BackupIndexConsistencySeverity.Warning,
            message,
            items.Count(item => string.IsNullOrWhiteSpace(externalIdSelector(item))),
            missing));
    }

    private static void AppendDuplicateExternalIdFindings<T>(
        ICollection<BackupIndexConsistencyFinding> findings,
        IEnumerable<T> items,
        Func<T, string?> externalIdSelector,
        string code,
        string message,
        Func<T, string> sampleFormatter)
    {
        var duplicates = items
            .Where(item => !string.IsNullOrWhiteSpace(externalIdSelector(item)))
            .GroupBy(item => externalIdSelector(item)!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicates.Count == 0)
            return;

        var samples = duplicates
            .Take(5)
            .Select(group => $"{group.Key} => {string.Join(", ", group.Select(sampleFormatter).OrderBy(static sample => sample, StringComparer.Ordinal).Take(3))}")
            .ToList();

        findings.Add(new BackupIndexConsistencyFinding(
            code,
            BackupIndexConsistencySeverity.Error,
            message,
            duplicates.Count,
            samples));
    }
}
