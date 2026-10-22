namespace VaultSync.Core.Models;

public enum DiskImageSourceKind { RegularFileFixture = 1, OfflineBlockDevice = 2 }

/// <summary>Bound source/tool claims, never authenticated provenance or native access authority.</summary>
public sealed record DiskImageCaptureOrigin(Guid SourceId, Guid ObservationId, uint PhysicalSectorBytes,
    DateTimeOffset CapturedAtUtc, string ToolVersion, string ToolSourceCommit, DiskImageGptLayout Layout)
{
    public DiskImageSourceKind SourceKind { get; init; } = DiskImageSourceKind.RegularFileFixture;
}

public sealed record DiskImageGptFileCaptureRequest(DiskImageFileCaptureRequest Image,
    Guid SourceId, Guid ObservationId, uint PhysicalSectorBytes, string ToolVersion, string ToolSourceCommit);
