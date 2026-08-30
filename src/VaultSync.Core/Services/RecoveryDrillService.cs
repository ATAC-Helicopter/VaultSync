using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Recoverability;

namespace VaultSync.Core.Services;

public sealed class RecoveryDrillService
{
    private const int MaximumExaminedFiles = 5_000;
    private const int MaximumPersistedFailureEvidence = 100;
    private sealed record PayloadAnalysisResult(int FilesExamined, bool IsLimited);

    public static bool HasPassedByteIntegrity(RecoveryDrillResult drill)
    {
        ArgumentNullException.ThrowIfNull(drill);
        try
        {
            IReadOnlyList<RecoveryDrillCheck> checks =
                JsonSerializer.Deserialize<List<RecoveryDrillCheck>>(drill.ChecksJson) ?? [];
            return checks.Any(check =>
                string.Equals(check.Code, "integrity", StringComparison.Ordinal) &&
                check.Status == RecoveryDrillCheckStatus.Passed);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [SuppressMessage(
        "Minor Code Smell",
        "S2325:Methods and properties that don't access instance data should be static",
        Justification = "Preserves the established public instance API for patch-release compatibility.")]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Preserves the established public instance API for patch-release compatibility.")]
    public async Task<RecoveryDrillResult> RunAsync(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        IReadOnlyCollection<FileEntry>? expectedFiles = null,
        CancellationToken cancellationToken = default) =>
        await RunAsyncCore(
            project,
            backup,
            snapshot,
            config,
            expectedFiles,
            cancellationToken).ConfigureAwait(false);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Preserves the established public instance API for patch-release compatibility.")]
    public async Task<RecoveryDrillResult> RunIsolatedRestoreAsync(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        IReadOnlyCollection<FileEntry>? expectedFiles = null,
        string? testRoot = null,
        CancellationToken cancellationToken = default)
    {
        RecoveryDrillResult baseline = await RunAsyncCore(
            project,
            backup,
            snapshot,
            config,
            expectedFiles,
            cancellationToken).ConfigureAwait(false);
        List<RecoveryDrillCheck> checks =
            JsonSerializer.Deserialize<List<RecoveryDrillCheck>>(baseline.ChecksJson) ?? [];

        try
        {
            checks.Add(await RunIsolatedRestoreCheckAsync(
                project,
                backup,
                snapshot,
                config,
                expectedFiles,
                testRoot,
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or
                                       UnauthorizedAccessException or
                                       InvalidDataException or
                                       InvalidOperationException)
        {
            checks.Add(new RecoveryDrillCheck(
                "isolated-restore",
                RecoveryDrillCheckStatus.Failed,
                $"The isolated restore could not be completed: {ex.Message}"));
        }

        RecoveryDrillStatus status = ResolveStatus(checks);
        return baseline with
        {
            RunUtc = DateTime.UtcNow,
            Status = status,
            ChecksPassed = checks.Count(check => check.Status == RecoveryDrillCheckStatus.Passed),
            ChecksTotal = checks.Count,
            IsLimited = baseline.IsLimited || status == RecoveryDrillStatus.Attention,
            Summary = status == RecoveryDrillStatus.Passed
                ? "Isolated recovery test passed. Representative content was restored and reopened outside the project."
                : "Isolated recovery test completed with evidence that needs attention.",
            ChecksJson = JsonSerializer.Serialize(checks)
        };
    }

    private static async Task<RecoveryDrillCheck> RunIsolatedRestoreCheckAsync(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        IReadOnlyCollection<FileEntry>? expectedFiles,
        string? testRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? contentPath = BackupContentPathResolver.Resolve(backup, config);
        if (contentPath is null)
            throw new DirectoryNotFoundException("The recorded recovery point is unavailable.");
        if (expectedFiles is not { Count: > 0 })
            throw new InvalidDataException("Snapshot file metadata is unavailable.");

        FileEntry[] selection = SelectRepresentativeFiles(expectedFiles);
        string target = BuildIsolatedRestoreTarget(project.Name, testRoot);
        Directory.CreateDirectory(target);
        SnapshotRestoreSelectionResult restored = await Task.Run(
            () => SnapshotExplorerService.RestoreSelection(
                contentPath,
                target,
                selection.Select(file => file.RelPath).ToArray()),
            cancellationToken).ConfigureAwait(false);
        int verified = await CountVerifiedFilesAsync(selection, target, cancellationToken).ConfigureAwait(false);
        bool passed = restored.FileCount == selection.Length && verified == selection.Length;

        return new RecoveryDrillCheck(
            "isolated-restore",
            passed ? RecoveryDrillCheckStatus.Passed : RecoveryDrillCheckStatus.Failed,
            passed
                ? $"{verified:N0} representative file(s) were restored into a new isolated folder and reopened successfully. Original project files were not touched."
                : $"{restored.FileCount:N0}/{selection.Length:N0} file(s) restored and {verified:N0}/{selection.Length:N0} reopened with matching evidence.",
            $"isolated_restore:{backup.Id}:{snapshot?.Id ?? backup.SnapshotId}",
            target);
    }

    private static FileEntry[] SelectRepresentativeFiles(IReadOnlyCollection<FileEntry> expectedFiles)
    {
        FileEntry[] selection = [.. expectedFiles
            .Where(file => file.Size >= 0 && file.Size <= 16 * 1024 * 1024)
            .OrderBy(file => file.RelPath, StringComparer.Ordinal)
            .Take(12)];
        return selection.Length > 0
            ? selection
            : throw new InvalidDataException("No representative files are eligible for the isolated restore.");
    }

    private static string BuildIsolatedRestoreTarget(string projectName, string? testRoot)
    {
        string root = string.IsNullOrWhiteSpace(testRoot)
            ? Path.Combine(Path.GetTempPath(), "VaultSync", "recovery-tests")
            : Path.GetFullPath(testRoot);
        string folderName = $"{SafeName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(root, folderName[..Math.Min(64, folderName.Length)]);
    }

    private static async Task<int> CountVerifiedFilesAsync(
        IEnumerable<FileEntry> files,
        string target,
        CancellationToken cancellationToken)
    {
        int verified = 0;
        foreach (FileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string restoredPath = Path.GetFullPath(Path.Combine(
                target,
                file.RelPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!BackupSafetyService.IsPathUnderRoot(target, restoredPath) || !File.Exists(restoredPath))
                continue;

            await using var stream = new FileStream(
                restoredPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(file.HashSha256) &&
                string.Equals(hash, file.HashSha256, StringComparison.OrdinalIgnoreCase))
            {
                verified++;
            }
        }

        return verified;
    }

    private static RecoveryDrillStatus ResolveStatus(IEnumerable<RecoveryDrillCheck> checks)
    {
        var statuses = checks.Select(check => check.Status).ToHashSet();
        if (statuses.Contains(RecoveryDrillCheckStatus.Failed))
            return RecoveryDrillStatus.Failed;

        return statuses.Contains(RecoveryDrillCheckStatus.Attention)
            ? RecoveryDrillStatus.Attention
            : RecoveryDrillStatus.Passed;
    }

    private static string SafeName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "project" : safe.Trim();
    }

    private static async Task<RecoveryDrillResult> RunAsyncCore(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        AppConfig config,
        IReadOnlyCollection<FileEntry>? expectedFiles,
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

        PayloadAnalysisResult payload = new(0, false);
        if (contentPath is not null)
        {
            try
            {
                payload = await AnalyzePayloadAsync(
                    project,
                    backup,
                    snapshot,
                    contentPath,
                    expectedFiles,
                    checks,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                checks.Add(new RecoveryDrillCheck(
                    "payload",
                    RecoveryDrillCheckStatus.Failed,
                    $"The recovery point could not be read: {ex.GetType().Name}."));
            }
        }

        RecoveryDrillStatus status = ResolveStatus(checks);
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
            FilesExamined = payload.FilesExamined,
            IsLimited = payload.IsLimited,
            Summary = summary,
            ChecksJson = JsonSerializer.Serialize(checks)
        };
    }

    private static async Task<PayloadAnalysisResult> AnalyzePayloadAsync(
        Project project,
        Backup backup,
        Snapshot? snapshot,
        string contentPath,
        IReadOnlyCollection<FileEntry>? expectedFiles,
        ICollection<RecoveryDrillCheck> checks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotFileInventory inventory = await Task.Run(
            () => SnapshotExplorerService.BuildFileInventory(
                contentPath,
                MaximumExaminedFiles,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        int filesExamined = inventory.Files.Count;
        bool encrypted = inventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive;
        AddPayloadCheck(checks, contentPath, inventory, filesExamined);
        AddInventoryCheck(checks, snapshot, inventory, filesExamined);
        bool limited = inventory.IsTruncated || encrypted;
        limited |= await AddIntegrityCheckAsync(
            project,
            backup,
            contentPath,
            expectedFiles,
            inventory,
            checks,
            cancellationToken).ConfigureAwait(false);
        return new PayloadAnalysisResult(filesExamined, limited);
    }

    private static void AddPayloadCheck(
        ICollection<RecoveryDrillCheck> checks,
        string contentPath,
        SnapshotFileInventory inventory,
        int filesExamined)
    {
        if (inventory.SourceKind != SnapshotExplorerSourceKind.EncryptedArchive)
        {
            checks.Add(new RecoveryDrillCheck(
                "payload",
                RecoveryDrillCheckStatus.Passed,
                $"The recovery point was opened and {filesExamined:N0} file entries were examined without restoring data."));
            return;
        }

        bool descriptorReadable = BackupArchiveCryptoService.TryReadDescriptor(
            contentPath,
            out _,
            out bool encrypted) && encrypted;
        checks.Add(new RecoveryDrillCheck(
            "payload",
            descriptorReadable ? RecoveryDrillCheckStatus.Attention : RecoveryDrillCheckStatus.Failed,
            descriptorReadable
                ? "The encrypted recovery point and its descriptor are readable; file-level checks require an unlock."
                : "The encrypted recovery point descriptor could not be read."));
    }

    private static async Task<bool> AddIntegrityCheckAsync(
        Project project,
        Backup backup,
        string contentPath,
        IReadOnlyCollection<FileEntry>? expectedFiles,
        SnapshotFileInventory inventory,
        ICollection<RecoveryDrillCheck> checks,
        CancellationToken cancellationToken)
    {
        if (inventory.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
            return false;

        if (expectedFiles is not { Count: > 0 })
        {
            checks.Add(new RecoveryDrillCheck(
                "integrity",
                RecoveryDrillCheckStatus.Attention,
                "Snapshot file hashes are unavailable, so this drill cannot claim byte-level recoverability.",
                "expected_hashes_unavailable:request"));
            return false;
        }

        RecoverabilityResult proof = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(
                backup.SnapshotId,
                DestinationMode: RecoverabilityDestinationMode.OriginalLocation,
                DestinationRoot: project.RootPath),
            contentPath,
            expectedFiles,
            MaximumExaminedFiles,
            RecoverabilityService.DefaultMaximumBytes,
            cancellationToken).ConfigureAwait(false);
        AddRecoverabilityChecks(checks, proof);
        return proof.IsLimited;
    }

    private static void AddRecoverabilityChecks(
        ICollection<RecoveryDrillCheck> checks,
        RecoverabilityResult proof)
    {
        RecoveryDrillCheckStatus integrityStatus = proof.Verdict switch
        {
            RecoverabilityVerdict.FullyRecoverable => RecoveryDrillCheckStatus.Passed,
            RecoverabilityVerdict.Inconclusive => RecoveryDrillCheckStatus.Attention,
            _ => RecoveryDrillCheckStatus.Failed
        };
        checks.Add(new RecoveryDrillCheck(
            "integrity",
            integrityStatus,
            $"{proof.Totals.VerifiedItems:N0}/{proof.Totals.SelectedItems:N0} files and {proof.Totals.VerifiedBytes:N0}/{proof.Totals.SelectedBytes:N0} bytes were verified against snapshot SHA-256 metadata.",
            $"integrity_summary:snapshot-{proof.SnapshotId}"));

        RecoveryDrillCheckStatus conflictStatus = proof.Totals.Conflicts > 0
            ? RecoveryDrillCheckStatus.Attention
            : RecoveryDrillCheckStatus.Passed;
        checks.Add(new RecoveryDrillCheck(
            "restore-plan",
            conflictStatus,
            proof.Totals.Conflicts > 0
                ? $"{proof.Totals.Conflicts:N0} newer destination file(s) would require a decision during an original-location restore."
                : "The read-only restore plan found no newer destination conflicts.",
            $"restore_plan:snapshot-{proof.SnapshotId}"));

        foreach (RecoverabilityEvidence evidence in proof.Items
                     .SelectMany(item => item.Evidence)
                     .Where(item => item.Severity is RecoverabilityEvidenceSeverity.Warning or RecoverabilityEvidenceSeverity.Error)
                     .Take(MaximumPersistedFailureEvidence))
        {
            checks.Add(new RecoveryDrillCheck(
                evidence.Code,
                evidence.Severity == RecoverabilityEvidenceSeverity.Error
                    ? RecoveryDrillCheckStatus.Failed
                    : RecoveryDrillCheckStatus.Attention,
                evidence.Message,
                evidence.Id,
                evidence.Path));
        }
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
