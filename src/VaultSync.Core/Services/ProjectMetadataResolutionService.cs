using System.Globalization;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public enum ProjectMetadataResolutionDecision
{
    KeepLocal,
    AcceptImported
}

public sealed record ProjectMetadataResolutionResult(
    string ProjectName,
    string ProjectExternalId,
    ProjectMetadataResolutionDecision Decision);

public sealed record ProjectMetadataUndoResult(
    string ProjectName,
    string ProjectExternalId);

/// <summary>
/// Applies a reviewed metadata conflict decision to local state and records the
/// durable merge evidence. Persistence remains with the caller so the project
/// mutation and configuration update can be presented as one user operation.
/// </summary>
public sealed class ProjectMetadataResolutionService(TimeProvider? timeProvider = null)
{
    private const int MaxResolutionRecords = 100;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProjectMetadataResolutionResult Resolve(
        SqliteRepository repository,
        AppConfig config,
        int projectId,
        string projectExternalId,
        ProjectMetadataResolutionDecision decision,
        Action<string, string>? applyAvatarColor = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(config);

        ProjectMetadataConflictRecord conflict = FindConflict(config, projectId, projectExternalId)
            ?? throw new InvalidOperationException("The metadata conflict is no longer pending.");
        Project current = repository.GetProjectById(conflict.ProjectId)
            ?? throw new InvalidOperationException("The project no longer exists.");
        ProjectMetadataConflictValues values = decision == ProjectMetadataResolutionDecision.AcceptImported
            ? SelectResult(conflict.AcceptImportedResult, conflict.Imported)
            : SelectResult(conflict.KeepLocalResult, conflict.Local);

        ApplyValues(repository, config, current, values, applyAvatarColor);
        RecordResolution(config, conflict, decision, values);
        AdvanceMergeBase(config, conflict);
        config.Advanced.ProjectMetadataConflicts.Remove(conflict);

        return new ProjectMetadataResolutionResult(current.Name, conflict.ProjectExternalId, decision);
    }

    public ProjectMetadataUndoResult UndoLatest(
        SqliteRepository repository,
        AppConfig config,
        Action<string, string>? applyAvatarColor = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(config);

        ProjectMetadataResolutionRecord resolution = (config.Advanced.ProjectMetadataResolutions ?? [])
            .Where(static item => item.UndoAvailable)
            .OrderByDescending(static item => item.ResolvedUtc, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The metadata resolution can no longer be undone.");
        Project current = repository.GetProjectByExternalId(resolution.ProjectExternalId)
            ?? throw new InvalidOperationException("The project no longer exists.");

        ApplyValues(repository, config, current, resolution.Local, applyAvatarColor);
        resolution.UndoAvailable = false;
        resolution.UndoneUtc = UtcNow();
        return new ProjectMetadataUndoResult(current.Name, resolution.ProjectExternalId);
    }

    private static ProjectMetadataConflictRecord? FindConflict(
        AppConfig config,
        int projectId,
        string projectExternalId) =>
        (config.Advanced.ProjectMetadataConflicts ?? []).FirstOrDefault(conflict =>
            conflict.ProjectId == projectId ||
            (!string.IsNullOrWhiteSpace(projectExternalId) &&
             string.Equals(conflict.ProjectExternalId, projectExternalId, StringComparison.OrdinalIgnoreCase)));

    private static ProjectMetadataConflictValues SelectResult(
        ProjectMetadataConflictValues? preferred,
        ProjectMetadataConflictValues? fallback) =>
        preferred?.AutoBackupEnabled.HasValue == true
            ? preferred
            : fallback ?? new ProjectMetadataConflictValues();

    private static void ApplyValues(
        SqliteRepository repository,
        AppConfig config,
        Project current,
        ProjectMetadataConflictValues values,
        Action<string, string>? applyAvatarColor)
    {
        repository.UpdateProjectEncryptionSettings(
            current.Id,
            string.IsNullOrWhiteSpace(values.EncryptionPolicy) ? current.EncryptionPolicy : values.EncryptionPolicy,
            current.EncryptionKeyRef);
        repository.UpdateProjectPreferredDestination(current.Id, EmptyToNull(values.PreferredDestinationId));
        repository.UpdateProjectRestoreMode(current.Id, EmptyToNull(values.RestoreMode));
        repository.UpdateProjectVerificationPolicy(current.Id, EmptyToNull(values.VerificationPolicy));
        repository.UpdateProjectTags(current.Id, EmptyToNull(values.Tags));
        ApplyAutoBackupSetting(config, current.Id, values.AutoBackupEnabled);
        if (!string.IsNullOrWhiteSpace(values.AvatarColor))
            applyAvatarColor?.Invoke(current.ExternalId, values.AvatarColor);
    }

    private void RecordResolution(
        AppConfig config,
        ProjectMetadataConflictRecord conflict,
        ProjectMetadataResolutionDecision decision,
        ProjectMetadataConflictValues result)
    {
        config.Advanced.ProjectMetadataResolutions ??= [];
        string resolvedUtc = UtcNow();
        foreach (ProjectMetadataResolutionRecord previous in config.Advanced.ProjectMetadataResolutions.Where(existing =>
                     existing.UndoAvailable &&
                     string.Equals(existing.ProjectExternalId, conflict.ProjectExternalId, StringComparison.OrdinalIgnoreCase)))
        {
            previous.UndoAvailable = false;
            previous.SupersededUtc = resolvedUtc;
        }

        config.Advanced.ProjectMetadataResolutions.RemoveAll(existing =>
            string.Equals(existing.ProjectExternalId, conflict.ProjectExternalId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.SourceMachineId, conflict.SourceMachineId, StringComparison.Ordinal) &&
            string.Equals(existing.SourceUpdatedUtc, conflict.SourceUpdatedUtc, StringComparison.Ordinal));
        config.Advanced.ProjectMetadataResolutions.Add(new ProjectMetadataResolutionRecord
        {
            SourceKey = conflict.SourceKey,
            ProjectExternalId = conflict.ProjectExternalId,
            SourceMachineId = conflict.SourceMachineId,
            SourceUpdatedUtc = conflict.SourceUpdatedUtc,
            SourceRevision = conflict.SourceRevision,
            BaseRevision = conflict.BaseRevision,
            Decision = decision == ProjectMetadataResolutionDecision.AcceptImported ? "accept-imported" : "keep-local",
            ResolvedUtc = resolvedUtc,
            UndoAvailable = true,
            Local = conflict.Local ?? new ProjectMetadataConflictValues(),
            Imported = conflict.Imported ?? new ProjectMetadataConflictValues(),
            Result = result
        });

        if (config.Advanced.ProjectMetadataResolutions.Count > MaxResolutionRecords)
        {
            config.Advanced.ProjectMetadataResolutions = config.Advanced.ProjectMetadataResolutions
                .OrderByDescending(static record => record.ResolvedUtc, StringComparer.Ordinal)
                .Take(MaxResolutionRecords)
                .ToList();
        }
    }

    private static void AdvanceMergeBase(AppConfig config, ProjectMetadataConflictRecord conflict)
    {
        config.Advanced.ProjectMetadataMergeBases ??= [];
        ProjectMetadataMergeBaseRecord? mergeBase = config.Advanced.ProjectMetadataMergeBases.FirstOrDefault(item =>
            string.Equals(item.SourceKey, conflict.SourceKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ProjectExternalId, conflict.ProjectExternalId, StringComparison.OrdinalIgnoreCase));
        mergeBase ??= new ProjectMetadataMergeBaseRecord
        {
            SourceKey = conflict.SourceKey,
            ProjectExternalId = conflict.ProjectExternalId
        };
        if (!config.Advanced.ProjectMetadataMergeBases.Contains(mergeBase))
            config.Advanced.ProjectMetadataMergeBases.Add(mergeBase);
        mergeBase.Revision = conflict.SourceRevision;
        mergeBase.WriterMachineId = conflict.SourceMachineId;
        mergeBase.UpdatedUtc = conflict.SourceUpdatedUtc;
        mergeBase.Values = conflict.Imported ?? new ProjectMetadataConflictValues();
    }

    private static void ApplyAutoBackupSetting(AppConfig config, int projectId, bool? enabled)
    {
        if (!enabled.HasValue)
            return;

        config.Backups.AutoBackupDisabledProjects ??= [];
        if (enabled.Value)
            config.Backups.AutoBackupDisabledProjects.Remove(projectId);
        else if (!config.Backups.AutoBackupDisabledProjects.Contains(projectId))
            config.Backups.AutoBackupDisabledProjects.Add(projectId);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string UtcNow() =>
        _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
