using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class DestinationOption(string id, string label)
{
    public string Id { get; } = id ?? string.Empty;
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is DestinationOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class ProjectGroupOption(string id, string label)
{
    public const string AllId = "all";
    public string Id { get; } = string.IsNullOrWhiteSpace(id) ? AllId : id.Trim();
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is ProjectGroupOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class EncryptionPolicyOption(string id, string label)
{
    public string Id { get; } = ProjectEncryptionPolicy.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is EncryptionPolicyOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class RestoreModeOption(string id, string label)
{
    public string Id { get; } = ProjectRestoreMode.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is RestoreModeOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class VerificationPolicyOption(string id, string label)
{
    public string Id { get; } = ProjectVerificationPolicy.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is VerificationPolicyOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class ProjectSnapshotViewModel(
    DateTime timestamp,
    long sizeBytes,
    int diffAdded = 0,
    int diffModified = 0,
    int diffDeleted = 0,
    long diffNetBytes = 0,
    IReadOnlyList<SnapshotDiffPathStat>? topChangedPaths = null)
{
    public DateTime Timestamp { get; } = timestamp;
    public long SizeBytes { get; } = sizeBytes;
    public int DiffAdded { get; } = Math.Max(0, diffAdded);
    public int DiffModified { get; } = Math.Max(0, diffModified);
    public int DiffDeleted { get; } = Math.Max(0, diffDeleted);
    public long DiffNetBytes { get; } = diffNetBytes;
    public IReadOnlyList<SnapshotDiffPathStat> TopChangedPaths { get; } = topChangedPaths ?? [];

    public double RelativeSize { get; set; }

    public double RelativeBarHeight => 24 + RelativeSize * 56;

    public double RelativeBarHeightCapped => Math.Max(16, RelativeBarHeight);

    public string TrendColor { get; set; } = "#2F3650";

    public bool ShowDayLabel { get; set; }

    public string DayLabel { get; set; } = string.Empty;

    public string DateDisplay => Timestamp.ToString("dd/MM/yyyy - HH:mm", CultureInfo.CurrentCulture);

    public string SizeDisplay => FormatSize(SizeBytes);

    public string DiffSummaryDisplay
    {
        get
        {
            var hasChanges = (DiffAdded > 0) || (DiffModified > 0) || (DiffDeleted > 0);
            if (!hasChanges && DiffNetBytes == 0)
                return L("Projects.DiffSummary.NoChanges", "No file changes detected or diff data is unavailable for this snapshot");

            return Lf(
                "Projects.DiffSummary.Compact",
                "+{0} / ~{1} / -{2}  Delta {3}",
                DiffAdded,
                DiffModified,
                DiffDeleted,
                FormatSignedSize(DiffNetBytes));
        }
    }

    public bool HasDiffTopPaths => TopChangedPaths.Count > 0;

    public string DiffTopPathsDisplay
    {
        get
        {
            if (TopChangedPaths.Count == 0)
                return L("Projects.DiffSummary.TopPaths.None", "Top paths: none");

            var preview = string.Join(
                ", ",
                TopChangedPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path.Path))
                    .Take(2)
                    .Select(path => $"{path.Path} ({path.Changes})"));

            return string.IsNullOrWhiteSpace(preview)
                ? L("Projects.DiffSummary.TopPaths.None", "Top paths: none")
                : Lf("Projects.DiffSummary.TopPaths.Compact", "Top paths: {0}", preview);
        }
    }

    public string TooltipText => $"{DateDisplay}\n{SizeDisplay}";

    public static string FormatSize(long bytes) =>
        UiFormat.FormatBytes(bytes, "0.0");

    private static string FormatSignedSize(long value)
        => UiFormat.FormatSignedBytes(value, "0.0");

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
