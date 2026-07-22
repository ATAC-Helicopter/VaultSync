using System.Text.Json;
using VaultSync.Core.Config;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class RecoveryDrillService
{
    private const int MaximumExaminedFiles = 5_000;

    public Task<RecoveryDrillResult> RunAsync(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(project, backup, snapshot, config, cancellationToken), cancellationToken);

    internal static RecoveryDrillResult Run(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(config);

        var checks = new List<RecoveryDrillCheck>();
        bool metadataMatches = backup.ProjectId == project.Id &&
                               snapshot is not null &&
                               snapshot.Id == backup.SnapshotId &&
                               snapshot.ProjectId == project.Id;
        checks.Add(new RecoveryDrillCheck(
            "metadata",
            metadataMatches ? RecoveryDrillCheckStatus.Passed : RecoveryDrillCheckStatus.Failed,
            metadataMatches
                ? "The project, backup, and snapshot records are linked correctly."
                : "The backup metadata is missing or does not link to this project."));

        string? contentPath = BackupContentPathResolver.Resolve(backup, config);
        bool available = contentPath is not null;
        checks.Add(new RecoveryDrillCheck(
            "availability",
            available ? RecoveryDrillCheckStatus.Passed : RecoveryDrillCheckStatus.Failed,
            available
                ? "The recovery point is currently reachable."
                : "The recovery point is not reachable at any recorded destination."));

        int filesExamined = 0;
        bool limited = false;
        if (available)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SnapshotFileInventory inventory = SnapshotExplorerService.BuildFileInventory(
                    contentPath!,
                    MaximumExaminedFiles,
                    cancellationToken);
                filesExamined = inventory.Files.Count;
                limited = inventory.IsTruncated || inventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive;

                if (inventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
                {
                    bool descriptorReadable = BackupArchiveCryptoService.TryReadDescriptor(contentPath!, out _, out bool encrypted) && encrypted;
                    checks.Add(new RecoveryDrillCheck(
                        "payload",
                        descriptorReadable ? RecoveryDrillCheckStatus.Attention : RecoveryDrillCheckStatus.Failed,
                        descriptorReadable
                            ? "The encrypted recovery point and its descriptor are readable; file-level checks require an unlock."
                            : "The encrypted recovery point descriptor could not be read."));
                }
                else
                {
                    checks.Add(new RecoveryDrillCheck(
                        "payload",
                        RecoveryDrillCheckStatus.Passed,
                        $"The recovery point was opened and {filesExamined:N0} file entries were examined without restoring data."));
                }

                AddInventoryCheck(checks, snapshot, inventory, filesExamined);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                checks.Add(new RecoveryDrillCheck(
                    "payload",
                    RecoveryDrillCheckStatus.Failed,
                    $"The recovery point could not be read: {ex.GetType().Name}."));
            }
        }

        RecoveryDrillStatus status = checks.Any(check => check.Status == RecoveryDrillCheckStatus.Failed)
            ? RecoveryDrillStatus.Failed
            : checks.Any(check => check.Status == RecoveryDrillCheckStatus.Attention)
                ? RecoveryDrillStatus.Attention
                : RecoveryDrillStatus.Passed;
        int passed = checks.Count(check => check.Status == RecoveryDrillCheckStatus.Passed);
        string summary = status switch
        {
            RecoveryDrillStatus.Passed => "Recovery drill passed. This point is reachable and its inventory is consistent.",
            RecoveryDrillStatus.Attention => "Recovery drill completed with limited or inconclusive checks.",
            _ => "Recovery drill failed. Review the failed checks before relying on this recovery point."
        };

        return new RecoveryDrillResult
        {
            ProjectId = project.Id,
            BackupId = backup.Id,
            SnapshotId = backup.SnapshotId,
            RunUtc = DateTime.UtcNow,
            Status = status,
            ChecksPassed = passed,
            ChecksTotal = checks.Count,
            FilesExamined = filesExamined,
            IsLimited = limited,
            Summary = summary,
            ChecksJson = JsonSerializer.Serialize(checks)
        };
    }

    private static void AddInventoryCheck(
        ICollection<RecoveryDrillCheck> checks,
        Snapshot? snapshot,
        SnapshotFileInventory inventory,
        int filesExamined)
    {
        if (inventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
            return;

        if (snapshot is null || snapshot.FileCount <= 0 || inventory.IsTruncated)
        {
            checks.Add(new RecoveryDrillCheck(
                "inventory",
                RecoveryDrillCheckStatus.Attention,
                inventory.IsTruncated
                    ? $"The inventory exceeded the {MaximumExaminedFiles:N0}-file drill limit."
                    : "No complete snapshot inventory is available for an exact count check."));
            return;
        }

        bool countMatches = snapshot.FileCount == filesExamined;
        checks.Add(new RecoveryDrillCheck(
            "inventory",
            countMatches ? RecoveryDrillCheckStatus.Passed : RecoveryDrillCheckStatus.Failed,
            countMatches
                ? $"The stored inventory matches all {snapshot.FileCount:N0} expected files."
                : $"The recovery point contains {filesExamined:N0} files; metadata expects {snapshot.FileCount:N0}."));
    }
}
