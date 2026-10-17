using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Checks a raw/explicit-zero logical map without reading bytes or authorizing restore.</summary>
public static class DiskImageCoverageService
{
    public const int MaximumParts = 4096;
    public const int MaximumExtents = 65536;

    public static DiskImageCoverageAssessment Assess(DiskImageCoveragePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DiskImageCoverageAssessment Invalid(string reason, ulong covered = 0) =>
            new(DiskImageCoverageState.Invalid, reason, covered);
        if (plan.LogicalSectorBytes is not (512 or 4096))
            return new(DiskImageCoverageState.Unsupported, "image.unsupported-sector-size", 0);
        if (plan.Capacity == 0 || plan.Capacity % plan.LogicalSectorBytes != 0)
            return Invalid("image.invalid-capacity");
        if (plan.Parts is null || plan.Extents is null)
            return Invalid("image.missing-map");
        if (plan.Parts.Count > MaximumParts || plan.Extents.Count > MaximumExtents)
            return Invalid("image.map-limit");
        var parts = new Dictionary<Guid, ulong>();
        foreach (DiskImagePayloadPart part in plan.Parts)
        {
            if (part is null || part.PartId == Guid.Empty || part.StoredLength == 0 ||
                !parts.TryAdd(part.PartId, part.StoredLength))
                return Invalid("image.invalid-part");
        }
        ulong cursor = 0;
        foreach (DiskImageExtent extent in plan.Extents)
        {
            if (extent is null || extent.Length == 0 || extent.Offset > plan.Capacity ||
                extent.Length > plan.Capacity - extent.Offset)
                return Invalid("image.invalid-range", cursor);
            if (extent.Offset != cursor)
                return Invalid(extent.Offset < cursor ? "image.overlap" : "image.coverage-gap", cursor);
            if (extent.Offset % plan.LogicalSectorBytes != 0 || extent.Length % plan.LogicalSectorBytes != 0)
                return Invalid("image.misaligned-range", cursor);
            if (!Enum.IsDefined(extent.Kind))
                return new(DiskImageCoverageState.Unsupported, "image.unsupported-extent", cursor);
            if (extent.Kind == DiskImageExtentKind.Zero)
            {
                if (extent.PartId is not null || extent.PartOffset != 0)
                    return Invalid("image.invalid-zero-rule", cursor);
            }
            else if (extent.PartOffset % plan.LogicalSectorBytes != 0)
                return Invalid("image.misaligned-part-offset", cursor);
            else if (extent.PartId is not Guid partId || !parts.TryGetValue(partId, out ulong length) ||
                     extent.PartOffset > length || extent.Length > length - extent.PartOffset)
                return Invalid("image.part-range-unavailable", cursor);
            cursor += extent.Length; // Bounds checked by subtraction above; cannot overflow.
        }
        if (cursor != plan.Capacity || !plan.HasCompletion)
            return new(DiskImageCoverageState.Incomplete, "image.incomplete", cursor);
        return new(DiskImageCoverageState.CompleteUnverified, "image.complete-unverified", cursor);
    }
}
