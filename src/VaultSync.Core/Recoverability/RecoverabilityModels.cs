using VaultSync.Core.Models;
using VaultSync.Core.Services;

namespace VaultSync.Core.Recoverability;

public static class RecoverabilitySchema
{
    public const string Version = "1.0";
}

public enum RecoverabilityVerdict
{
    FullyRecoverable,
    PartiallyRecoverable,
    Unrecoverable,
    Inconclusive
}

public enum RecoverabilityItemVerdict
{
    Verified,
    Unavailable,
    Corrupted,
    Inconclusive
}

public enum RecoverabilityRestoreAction
{
    Create,
    Overwrite,
    SkipIdentical,
    Conflict,
    Unavailable,
    NotEvaluated
}

public enum RecoverabilityEvidenceSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public enum RecoverabilityDestinationMode
{
    SafeCopy,
    OriginalLocation
}

public sealed record RecoverabilityRequest(
    int SnapshotId,
    string Path = "",
    bool IncludeChildren = true,
    RecoverabilityDestinationMode DestinationMode = RecoverabilityDestinationMode.SafeCopy,
    string? DestinationRoot = null);

public sealed record RecoverabilityEvidence(
    string Id,
    string Code,
    RecoverabilityEvidenceSeverity Severity,
    string Message,
    string? Path = null);

public sealed record RecoverabilityItem(
    FileEntry File,
    RecoverabilityItemVerdict Verdict,
    RecoverabilityRestoreAction Action,
    IReadOnlyList<RecoverabilityEvidence> Evidence);

public sealed record RecoverabilityTotals(
    int SelectedItems,
    int VerifiedItems,
    int UnavailableItems,
    int CorruptedItems,
    int InconclusiveItems,
    int Conflicts,
    long SelectedBytes,
    long VerifiedBytes,
    int PlannedOperations);

public sealed record RecoverabilityResult
{
    public string SchemaVersion { get; init; } = RecoverabilitySchema.Version;
    public int SnapshotId { get; init; }
    public string RequestedPath { get; init; } = string.Empty;
    public RecoverabilityVerdict Verdict { get; init; }
    public bool IsLimited { get; init; }
    public RecoverabilityTotals Totals { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public IReadOnlyList<RecoverabilityItem> Items { get; init; } = [];
    public IReadOnlyList<RecoverabilityEvidence> Evidence { get; init; } = [];
}

public sealed record StoredFileObservation(
    string Path,
    bool Exists,
    long? Size,
    DateTime? ModifiedUtc,
    string? HashSha256,
    bool WasRead,
    string? FailureCode = null);

public sealed record StoredContentEvidence(
    SnapshotExplorerSourceKind SourceKind,
    IReadOnlyDictionary<string, StoredFileObservation> Files,
    bool IsLimited,
    long BytesRead);
