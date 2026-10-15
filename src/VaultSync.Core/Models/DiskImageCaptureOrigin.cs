namespace VaultSync.Core.Models;

/// <summary>Bound source/tool claims for the regular-file fixture profile, not authenticated provenance.</summary>
public sealed record DiskImageCaptureOrigin(Guid SourceId, Guid ObservationId, uint PhysicalSectorBytes,
    DateTimeOffset CapturedAtUtc, string ToolVersion, string ToolSourceCommit, DiskImageGptLayout Layout);

public sealed record DiskImageGptFileCaptureRequest(DiskImageFileCaptureRequest Image,
    Guid SourceId, Guid ObservationId, uint PhysicalSectorBytes, string ToolVersion, string ToolSourceCommit);
