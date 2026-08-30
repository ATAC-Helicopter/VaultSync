using System.Security.Cryptography;
using VaultSync.Core.Models;
using VaultSync.Core.Services;

namespace VaultSync.Core.Recoverability;

/// <summary>
/// Produces a deterministic, read-only recovery proof. This service never creates,
/// overwrites, or deletes user files.
/// </summary>
public static class RecoverabilityService
{
    public const int DefaultMaximumFiles = 5_000;
    public const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<RecoverabilityResult> AnalyzeAsync(
        RecoverabilityRequest request,
        string contentRoot,
        IReadOnlyCollection<FileEntry> expectedFiles,
        int maximumFiles = DefaultMaximumFiles,
        long maximumBytes = DefaultMaximumBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(request, contentRoot, expectedFiles, maximumFiles, maximumBytes);

        string requestedPath = NormalizePath(request.Path, allowEmpty: true);
        List<FileEntry> matched = SelectFiles(expectedFiles, requestedPath, request.IncludeChildren);
        bool selectionLimited = matched.Count > maximumFiles;
        List<FileEntry> selected = selectionLimited ? [.. matched.Take(maximumFiles)] : matched;
        var evidence = new EvidenceCollector();
        evidence.Add(
            matched.Count > 0 ? "path_found" : "path_not_found",
            matched.Count > 0 ? RecoverabilityEvidenceSeverity.Success : RecoverabilityEvidenceSeverity.Error,
            matched.Count > 0
                ? $"Selected {selected.Count:N0} of {matched.Count:N0} matching file(s) from the requested recovery path."
                : "The requested path is not present in the selected snapshot.",
            requestedPath);

        if (matched.Count == 0)
        {
            return CreateResult(
                request,
                requestedPath,
                RecoverabilityVerdict.Unrecoverable,
                isLimited: false,
                [],
                evidence.All);
        }

        if (selectionLimited)
        {
            evidence.Add(
                "selection_limit_reached",
                RecoverabilityEvidenceSeverity.Warning,
                $"{matched.Count - selected.Count:N0} additional matching file(s) were omitted by the {maximumFiles:N0}-file proof limit.");
        }

        StoredContentEvidence stored = await SnapshotExplorerService.ReadStoredFileEvidenceAsync(
            contentRoot,
            selected,
            maximumFiles,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);

        if (stored.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
        {
            evidence.Add(
                "encrypted_content_locked",
                RecoverabilityEvidenceSeverity.Warning,
                "The encrypted archive is present, but its file bytes were not unlocked for this proof.");
        }

        var items = new List<RecoverabilityItem>(selected.Count);
        foreach (FileEntry file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stored.Files.TryGetValue(file.RelPath, out StoredFileObservation? observation);
            if (observation is null && stored.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
            {
                observation = new StoredFileObservation(
                    file.RelPath,
                    Exists: true,
                    Size: null,
                    ModifiedUtc: null,
                    HashSha256: null,
                    WasRead: false,
                    FailureCode: "encrypted_content_locked");
            }
            items.Add(await EvaluateItemAsync(
                file,
                observation,
                request,
                cancellationToken).ConfigureAwait(false));
        }

        foreach (RecoverabilityEvidence itemEvidence in items.SelectMany(item => item.Evidence))
            evidence.Add(itemEvidence);

        if (stored.IsLimited)
        {
            evidence.Add(
                "verification_limit_reached",
                RecoverabilityEvidenceSeverity.Warning,
                $"The proof stopped at its safety limit ({maximumFiles:N0} files or {FormatBytes(maximumBytes)} read).");
        }

        RecoverabilityVerdict verdict = DetermineVerdict(items);
        return CreateResult(request, requestedPath, verdict, stored.IsLimited || selectionLimited, items, evidence.All);
    }

    private static void ValidateInputs(
        RecoverabilityRequest request,
        string contentRoot,
        IReadOnlyCollection<FileEntry> expectedFiles,
        int maximumFiles,
        long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentNullException.ThrowIfNull(expectedFiles);
        if (maximumFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    private static async Task<RecoverabilityItem> EvaluateItemAsync(
        FileEntry file,
        StoredFileObservation? observation,
        RecoverabilityRequest request,
        CancellationToken cancellationToken)
    {
        var evidence = new EvidenceCollector(file.RelPath);
        RecoverabilityItemVerdict verdict;
        if (observation is null || !observation.Exists)
        {
            verdict = observation?.FailureCode == "verification_limit_reached"
                ? RecoverabilityItemVerdict.Inconclusive
                : RecoverabilityItemVerdict.Unavailable;
            evidence.Add(
                observation?.FailureCode ?? "object_missing",
                verdict == RecoverabilityItemVerdict.Inconclusive
                    ? RecoverabilityEvidenceSeverity.Warning
                    : RecoverabilityEvidenceSeverity.Error,
                verdict == RecoverabilityItemVerdict.Inconclusive
                    ? "The file was not read because the bounded proof limit was reached."
                    : "The file is missing from the stored recovery point.",
                file.RelPath);
        }
        else if (!observation.WasRead || string.IsNullOrWhiteSpace(observation.HashSha256))
        {
            verdict = RecoverabilityItemVerdict.Inconclusive;
            evidence.Add(
                observation.FailureCode ?? "object_unavailable",
                RecoverabilityEvidenceSeverity.Warning,
                "VaultSync could not read the complete stored file, so integrity was not claimed.",
                file.RelPath);
        }
        else if (observation.Size != file.Size)
        {
            verdict = RecoverabilityItemVerdict.Corrupted;
            evidence.Add(
                "size_mismatch",
                RecoverabilityEvidenceSeverity.Error,
                $"Stored size {observation.Size.GetValueOrDefault():N0} B does not match expected size {file.Size:N0} B.",
                file.RelPath);
        }
        else if (string.IsNullOrWhiteSpace(file.HashSha256))
        {
            verdict = RecoverabilityItemVerdict.Inconclusive;
            evidence.Add(
                "expected_hash_missing",
                RecoverabilityEvidenceSeverity.Warning,
                "Snapshot metadata has no expected hash, so the stored bytes cannot be independently verified.",
                file.RelPath);
        }
        else if (!string.Equals(observation.HashSha256, file.HashSha256, StringComparison.OrdinalIgnoreCase))
        {
            verdict = RecoverabilityItemVerdict.Corrupted;
            evidence.Add(
                "hash_mismatch",
                RecoverabilityEvidenceSeverity.Error,
                "The stored SHA-256 hash does not match snapshot metadata.",
                file.RelPath);
        }
        else
        {
            verdict = RecoverabilityItemVerdict.Verified;
            evidence.Add(
                "hash_match",
                RecoverabilityEvidenceSeverity.Success,
                "The stored bytes match the expected SHA-256 hash and size.",
                file.RelPath);
        }

        RecoverabilityRestoreAction action = await DetermineActionAsync(
            file,
            verdict,
            request,
            evidence,
            cancellationToken).ConfigureAwait(false);
        return new RecoverabilityItem(file, verdict, action, evidence.All);
    }

    private static async Task<RecoverabilityRestoreAction> DetermineActionAsync(
        FileEntry file,
        RecoverabilityItemVerdict verdict,
        RecoverabilityRequest request,
        EvidenceCollector evidence,
        CancellationToken cancellationToken)
    {
        if (verdict is RecoverabilityItemVerdict.Unavailable or RecoverabilityItemVerdict.Corrupted)
            return RecoverabilityRestoreAction.Unavailable;
        if (verdict == RecoverabilityItemVerdict.Inconclusive)
            return RecoverabilityRestoreAction.NotEvaluated;
        if (request.DestinationMode == RecoverabilityDestinationMode.SafeCopy ||
            string.IsNullOrWhiteSpace(request.DestinationRoot))
        {
            return RecoverabilityRestoreAction.Create;
        }

        string destination = ResolveUnderRoot(request.DestinationRoot, file.RelPath);
        EnsureNoLinkedPathComponents(request.DestinationRoot, destination);
        if (!File.Exists(destination))
            return RecoverabilityRestoreAction.Create;

        var info = new FileInfo(destination);
        if (info.Length == file.Size && !string.IsNullOrWhiteSpace(file.HashSha256))
        {
            await using var stream = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (string.Equals(Convert.ToHexString(hash), file.HashSha256, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(
                    "destination_identical",
                    RecoverabilityEvidenceSeverity.Info,
                    "The original destination already contains identical bytes.",
                    file.RelPath);
                return RecoverabilityRestoreAction.SkipIdentical;
            }
        }

        bool newer = info.LastWriteTimeUtc > file.MTimeUtc;
        evidence.Add(
            "destination_conflict",
            newer ? RecoverabilityEvidenceSeverity.Warning : RecoverabilityEvidenceSeverity.Info,
            newer
                ? "The destination contains a newer, different file; an original-location restore would require a decision."
                : "The destination contains different bytes and would be overwritten by an approved original-location restore.",
            file.RelPath);
        return newer ? RecoverabilityRestoreAction.Conflict : RecoverabilityRestoreAction.Overwrite;
    }

    private static List<FileEntry> SelectFiles(
        IReadOnlyCollection<FileEntry> expectedFiles,
        string requestedPath,
        bool includeChildren)
    {
        var unique = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        foreach (FileEntry file in expectedFiles)
        {
            string path = NormalizePath(file.RelPath, allowEmpty: false);
            if (!unique.TryAdd(path, file with { RelPath = path }))
                throw new InvalidDataException($"Snapshot metadata contains duplicate file path '{path}'.");
        }

        if (requestedPath.Length == 0)
            return [.. unique.Values.OrderBy(file => file.RelPath, StringComparer.Ordinal)];

        string prefix = requestedPath + "/";
        return [.. unique.Values
            .Where(file =>
                string.Equals(file.RelPath, requestedPath, StringComparison.Ordinal) ||
                (includeChildren && file.RelPath.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(file => file.RelPath, StringComparer.Ordinal)];
    }

    internal static string NormalizePath(string? path, bool allowEmpty)
    {
        string converted = (path ?? string.Empty).Replace('\\', '/');
        bool hasRootPrefix = converted.StartsWith("/", StringComparison.Ordinal) ||
                             (converted.Length >= 2 && char.IsLetter(converted[0]) && converted[1] == ':');
        string raw = converted.TrimEnd('/');
        if (raw.Length == 0 && allowEmpty && !hasRootPrefix)
            return string.Empty;
        if (raw.Length == 0 ||
            hasRootPrefix ||
            raw.Contains('\0', StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(raw) ||
            raw.Split('/').Any(component => component is "" or "." or ".."))
        {
            throw new InvalidDataException($"Path '{path}' is not a safe snapshot-relative path.");
        }

        return raw;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        string rawRoot = Path.GetFullPath(root);
        string pathRoot = Path.GetPathRoot(rawRoot) ?? string.Empty;
        string trimmedRoot = rawRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = trimmedRoot.Length == 0 || trimmedRoot.Length < pathRoot.Length
            ? pathRoot
            : trimmedRoot;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison))
            throw new InvalidDataException($"Path '{relativePath}' escapes the destination root.");
        return candidate;
    }

    private static void EnsureNoLinkedPathComponents(string root, string destination)
    {
        string fullRoot = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(fullRoot, destination);
        string current = fullRoot;
        foreach (string component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Destination path '{relative}' contains a linked component.");
        }
    }

    private static RecoverabilityVerdict DetermineVerdict(IReadOnlyCollection<RecoverabilityItem> items)
    {
        int verified = items.Count(item => item.Verdict == RecoverabilityItemVerdict.Verified);
        if (verified == items.Count)
            return RecoverabilityVerdict.FullyRecoverable;
        if (verified > 0)
            return RecoverabilityVerdict.PartiallyRecoverable;
        if (items.Any(item => item.Verdict == RecoverabilityItemVerdict.Inconclusive))
            return RecoverabilityVerdict.Inconclusive;
        return RecoverabilityVerdict.Unrecoverable;
    }

    private static RecoverabilityResult CreateResult(
        RecoverabilityRequest request,
        string requestedPath,
        RecoverabilityVerdict verdict,
        bool isLimited,
        IReadOnlyList<RecoverabilityItem> items,
        IReadOnlyList<RecoverabilityEvidence> evidence)
    {
        IReadOnlyList<RecoverabilityItem> files = items;
        var totals = new RecoverabilityTotals(
            files.Count,
            files.Count(item => item.Verdict == RecoverabilityItemVerdict.Verified),
            files.Count(item => item.Verdict == RecoverabilityItemVerdict.Unavailable),
            files.Count(item => item.Verdict == RecoverabilityItemVerdict.Corrupted),
            files.Count(item => item.Verdict == RecoverabilityItemVerdict.Inconclusive),
            files.Count(item => item.Action == RecoverabilityRestoreAction.Conflict),
            files.Sum(item => item.File.Size),
            files.Where(item => item.Verdict == RecoverabilityItemVerdict.Verified).Sum(item => item.File.Size),
            files.Count(item => item.Action is RecoverabilityRestoreAction.Create or RecoverabilityRestoreAction.Overwrite));
        return new RecoverabilityResult
        {
            SnapshotId = request.SnapshotId,
            RequestedPath = requestedPath,
            Verdict = verdict,
            IsLimited = isLimited,
            Totals = totals,
            Items = items,
            Evidence = evidence
        };
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB"
            : $"{bytes / (1024d * 1024):0.##} MiB";

    private sealed class EvidenceCollector
    {
        private readonly string? _scope;
        private readonly List<RecoverabilityEvidence> _items = [];

        public EvidenceCollector(string? scope = null) => _scope = scope;

        public IReadOnlyList<RecoverabilityEvidence> All => _items;

        public void Add(
            string code,
            RecoverabilityEvidenceSeverity severity,
            string message,
            string? path = null)
        {
            string effectiveScope = path ?? _scope ?? "request";
            string id = $"{code}:{effectiveScope}";
            _items.Add(new RecoverabilityEvidence(id, code, severity, message, path ?? _scope));
        }

        public void Add(RecoverabilityEvidence evidence) => _items.Add(evidence);
    }
}
