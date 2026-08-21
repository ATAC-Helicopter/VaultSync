using System;
using System.Collections.Generic;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public sealed record ProjectMetadataMergePlan(
    ProjectMetadataConflictValues Merged,
    ProjectMetadataConflictValues KeepLocalResult,
    ProjectMetadataConflictValues AcceptImportedResult,
    IReadOnlyList<string> ConflictingFields)
{
    public bool HasConflicts => ConflictingFields.Count > 0;
}

public static class ProjectMetadataMergePlanner
{
    private sealed record MergeAssignments<T>(
        Action<T> Merged,
        Action<T> KeepLocal,
        Action<T> AcceptImported);

    public static ProjectMetadataMergePlan Create(
        ProjectMetadataConflictValues? mergeBase,
        ProjectMetadataConflictValues local,
        ProjectMetadataConflictValues imported)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(imported);

        var merged = new ProjectMetadataConflictValues();
        var keepLocal = new ProjectMetadataConflictValues();
        var acceptImported = new ProjectMetadataConflictValues();
        var conflicts = new List<string>();

        Merge("avatarColor", mergeBase?.AvatarColor, local.AvatarColor, imported.AvatarColor,
            StringComparer.OrdinalIgnoreCase, new(value => merged.AvatarColor = value,
                value => keepLocal.AvatarColor = value, value => acceptImported.AvatarColor = value), conflicts);
        Merge("encryptionPolicy", mergeBase?.EncryptionPolicy, local.EncryptionPolicy, imported.EncryptionPolicy,
            StringComparer.OrdinalIgnoreCase, new(value => merged.EncryptionPolicy = value,
                value => keepLocal.EncryptionPolicy = value, value => acceptImported.EncryptionPolicy = value), conflicts);
        Merge("preferredDestinationId", mergeBase?.PreferredDestinationId, local.PreferredDestinationId, imported.PreferredDestinationId,
            StringComparer.OrdinalIgnoreCase, new(value => merged.PreferredDestinationId = value,
                value => keepLocal.PreferredDestinationId = value, value => acceptImported.PreferredDestinationId = value), conflicts);
        Merge("restoreMode", mergeBase?.RestoreMode, local.RestoreMode, imported.RestoreMode,
            StringComparer.OrdinalIgnoreCase, new(value => merged.RestoreMode = value,
                value => keepLocal.RestoreMode = value, value => acceptImported.RestoreMode = value), conflicts);
        Merge("verificationPolicy", mergeBase?.VerificationPolicy, local.VerificationPolicy, imported.VerificationPolicy,
            StringComparer.OrdinalIgnoreCase, new(value => merged.VerificationPolicy = value,
                value => keepLocal.VerificationPolicy = value, value => acceptImported.VerificationPolicy = value), conflicts);
        Merge("autoBackupEnabled", mergeBase?.AutoBackupEnabled, local.AutoBackupEnabled, imported.AutoBackupEnabled,
            EqualityComparer<bool?>.Default, new(value => merged.AutoBackupEnabled = value,
                value => keepLocal.AutoBackupEnabled = value, value => acceptImported.AutoBackupEnabled = value), conflicts);
        Merge("tags", mergeBase?.Tags, local.Tags, imported.Tags,
            StringComparer.Ordinal, new(value => merged.Tags = value,
                value => keepLocal.Tags = value, value => acceptImported.Tags = value), conflicts);

        return new ProjectMetadataMergePlan(merged, keepLocal, acceptImported, conflicts);
    }

    private static void Merge<T>(
        string field,
        T? baseValue,
        T local,
        T imported,
        IEqualityComparer<T> comparer,
        MergeAssignments<T> assignments,
        List<string> conflicts)
    {
        if (comparer.Equals(local, imported))
        {
            assignments.Merged(local);
            assignments.KeepLocal(local);
            assignments.AcceptImported(local);
            return;
        }

        // With no trusted base, a difference must be reviewed. This is the
        // conservative behavior required for stores written before 1.8.7.
        if (baseValue is null)
        {
            conflicts.Add(field);
            assignments.Merged(local);
            assignments.KeepLocal(local);
            assignments.AcceptImported(imported);
            return;
        }

        bool localChanged = !comparer.Equals(local, baseValue);
        bool importedChanged = !comparer.Equals(imported, baseValue);
        if (localChanged && importedChanged)
        {
            conflicts.Add(field);
            assignments.Merged(local);
            assignments.KeepLocal(local);
            assignments.AcceptImported(imported);
            return;
        }

        T value = importedChanged ? imported : local;
        assignments.Merged(value);
        assignments.KeepLocal(value);
        assignments.AcceptImported(value);
    }
}
