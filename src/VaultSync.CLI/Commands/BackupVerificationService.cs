using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Commands;

internal static class BackupVerificationService
{
    internal sealed record FileFailure(string Path, string Reason);

    internal sealed record Outcome(int ProjectId, int BackupId, int SnapshotId, int CheckedFiles,
        int PassedFiles, int FailedFiles, IReadOnlyList<FileFailure> Failures, int FailureLimit,
        bool FailuresTruncated, bool PayloadChecked, string? ErrorCode, string? ErrorMessage,
        int ExitCode)
    {
        public object Details => new
        {
            projectId = ProjectId,
            backupId = BackupId,
            snapshotId = SnapshotId,
            checkedFiles = CheckedFiles,
            passedFiles = PassedFiles,
            failedFiles = FailedFiles,
            failures = Failures,
            failureLimit = FailureLimit,
            failuresTruncated = FailuresTruncated,
            payloadChecked = PayloadChecked
        };
    }

    public static async Task<Outcome> CheckAsync(SqliteRepository repository, Project project, Backup backup,
        AppConfig config, int failureLimit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!repository.GetSnapshotsForProject(project.Name).Any(snapshot => snapshot.Id == backup.SnapshotId))
            return Error(project, backup, failureLimit, "snapshot_not_found",
                "The backup does not reference a snapshot owned by the selected project.", 2);

        if (backup.IsEncrypted || !string.Equals(backup.BackupMode, BackupModes.Full, StringComparison.OrdinalIgnoreCase))
            return Error(project, backup, failureLimit, "unsupported_backup_format",
                "This CLI check supports full, unencrypted folder backups only.", 2);

        string? sourceRoot = BackupContentPathResolver.Resolve(backup, config);
        if (sourceRoot is null || !Directory.Exists(sourceRoot))
            return Error(project, backup, failureLimit, "backup_unavailable",
                "The recorded backup folder is unavailable at its selected destination.", 1);
        if (File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.PlainArchiveFileName)) ||
            File.Exists(Path.Combine(sourceRoot, BackupArchiveCryptoService.EncryptedArchiveFileName)))
            return Error(project, backup, failureLimit, "unsupported_backup_format",
                "This CLI check supports recorded folder backups only; archive data requires a qualified verifier.", 2);

        FileEntry[] files = repository.GetFilesForSnapshot(backup.SnapshotId).ToArray();
        if (files.Length == 0)
            return Error(project, backup, failureLimit, "snapshot_history_empty",
                "The backup's snapshot has no indexed files to verify.", 2);

        var hash = new HashService();
        var failures = new List<FileFailure>();
        int failedCount = 0;
        foreach (FileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileFailure? failure = await CheckFileAsync(sourceRoot, file, hash, cancellationToken)
                .ConfigureAwait(false);
            if (failure is null)
                continue;
            failedCount++;
            if (failures.Count < failureLimit)
                failures.Add(failure);
        }

        return new Outcome(project.Id, backup.Id, backup.SnapshotId, files.Length,
            files.Length - failedCount, failedCount, failures, failureLimit,
            failedCount > failures.Count, true,
            failedCount == 0 ? null : "verification_failed",
            failedCount == 0 ? null : "Recorded backup bytes did not match the selected snapshot.",
            failedCount == 0 ? 0 : 1);
    }

    public static Outcome Error(Project project, Backup backup, int failureLimit, string code,
        string message, int exitCode) =>
        new(project.Id, backup.Id, backup.SnapshotId, 0, 0, 0, [], failureLimit,
            false, false, code, message, exitCode);

    private static async Task<FileFailure?> CheckFileAsync(string sourceRoot, FileEntry file, HashService hash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.HashSha256))
            return new FileFailure(file.RelPath, "hash_unavailable");
        if (!BackupSafetyService.TryResolveExistingFileUnderRoot(sourceRoot, file.RelPath, out string sourcePath))
            return new FileFailure(file.RelPath, "missing_or_unsafe");
        try
        {
            string actual = await hash.Sha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
            return string.Equals(actual, file.HashSha256, StringComparison.OrdinalIgnoreCase)
                ? null : new FileFailure(file.RelPath, "hash_mismatch");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new FileFailure(file.RelPath, "read_error");
        }
    }
}
