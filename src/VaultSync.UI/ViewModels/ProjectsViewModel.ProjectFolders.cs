using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels;

public partial class ProjectsViewModel
{
    private void LoadGroupOptions()
    {
        string selectedProjectGroupId = SelectedProject?.GroupId ?? ProjectGroupOption.UngroupedId;
        GroupOptions.Clear();
        GroupOptions.Add(new ProjectGroupOption(ProjectGroupOption.UngroupedId, L("Projects.Folder.Ungrouped", "Ungrouped")));

        try
        {
            SqliteRepository repo = CreateRepository(_configStore.GetSnapshot());
            repo.EnsureSchema();
            foreach (ProjectGroup group in repo.GetProjectGroups())
                GroupOptions.Add(new ProjectGroupOption(group.Id, group.Name));
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project folders could not be loaded: {ex.GetType().Name} - {ex.Message}");
        }

        Interlocked.Increment(ref _suppressProjectPersistence);
        try
        {
            foreach (ProjectItemViewModel project in _allProjects)
                SetProjectGroupOption(project);

            if (SelectedProject is not null)
            {
                ProjectGroupOption selected = GroupOptions.FirstOrDefault(option =>
                    string.Equals(option.Id, selectedProjectGroupId, StringComparison.OrdinalIgnoreCase))
                    ?? GroupOptions[0];
                SelectedProject.SetGroupOption(selected);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _suppressProjectPersistence);
        }
    }

    private void SetProjectGroupOption(ProjectItemViewModel project)
    {
        ProjectGroupOption option = GroupOptions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, project.GroupId, StringComparison.OrdinalIgnoreCase))
            ?? GroupOptions[0];
        project.SetGroupOption(option);
    }

    private void RebuildProjectFolders(IReadOnlyList<ProjectItemViewModel> visibleProjects)
    {
        Dictionary<string, ProjectFolderViewModel> existing = ProjectFolders
            .ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        bool includeEmptyFolders = string.IsNullOrWhiteSpace(SearchText);
        var rebuilt = new List<ProjectFolderViewModel>();

        foreach (ProjectGroupOption option in GroupOptions.Where(option =>
                     !string.Equals(option.Id, ProjectGroupOption.UngroupedId, StringComparison.OrdinalIgnoreCase)))
        {
            List<ProjectItemViewModel> allMembers = [.. _allProjects.Where(project =>
                string.Equals(project.GroupId, option.Id, StringComparison.OrdinalIgnoreCase))];
            bool folderMatchesSearch = !string.IsNullOrWhiteSpace(SearchText) &&
                option.Label.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
            List<ProjectItemViewModel> members = folderMatchesSearch
                ? allMembers
                : [.. visibleProjects.Where(project =>
                    string.Equals(project.GroupId, option.Id, StringComparison.OrdinalIgnoreCase))];
            if (!includeEmptyFolders && members.Count == 0)
                continue;

            ProjectFolderViewModel folder = existing.TryGetValue(option.Id, out ProjectFolderViewModel? current)
                ? current
                : new ProjectFolderViewModel(
                    new ProjectGroup
                    {
                        Id = option.Id,
                        Name = option.Label,
                        SortOrder = rebuilt.Count
                    },
                    L("Projects.Folder.Ungrouped", "Ungrouped"));
            folder.ReplaceProjects(members, allMembers);
            rebuilt.Add(folder);
        }

        List<ProjectItemViewModel> ungroupedProjects = [.. visibleProjects.Where(project =>
            string.IsNullOrWhiteSpace(project.GroupId) ||
            GroupOptions.All(option => !string.Equals(option.Id, project.GroupId, StringComparison.OrdinalIgnoreCase)))];

        ProjectFolders.SyncWith(rebuilt);
        UngroupedProjects.SyncWith(ungroupedProjects);

        OnPropertiesChanged(nameof(HasProjectFolders), nameof(HasUngroupedProjects));

        RaiseProjectGroupCommandStates();
    }

    private bool CanCreateProjectGroup() =>
        !string.IsNullOrWhiteSpace(ProjectGroup.NormalizeName(NewProjectGroupName));

    private void MoveSelectedProjectToFolder()
    {
        ProjectItemViewModel? project = SelectedProject;
        ProjectGroupOption? destination = project?.SelectedGroupOption;
        if (project is not { IsRegistered: true, HasPendingGroupChange: true } || destination is null)
            return;

        try
        {
            SqliteRepository repo = CreateRepository(_configStore.GetSnapshot());
            if (!repo.SetProjectGroup(project.ProjectId, destination.Id))
                throw new InvalidOperationException(L("Projects.Folder.Missing", "That folder no longer exists."));

            Interlocked.Increment(ref _suppressProjectPersistence);
            try
            {
                project.CommitGroupOption(destination);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressProjectPersistence);
            }

            MoveProjectCardToCommittedFolder(project);
            ProjectSettingsMetadataChanged?.Invoke(project.ProjectId);
            _moveSelectedProjectToFolderCommand.RaiseCanExecuteChanged();

            string message = string.IsNullOrWhiteSpace(destination.Id)
                ? Lf("Projects.Folder.MovedToMain", "Moved “{0}” to the main project list.", project.Name)
                : Lf("Projects.Folder.MovedInside", "Moved “{0}” into “{1}”.", project.Name, destination.Label);
            ShowNotification(message, NotificationSeverity.Info);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetProjectGroupOption(project);
            ShowNotification(ex.Message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetProjectGroupOption(project);
            ShowNotification(
                Lf("Projects.Folder.MoveFailed", "Could not move the project: {0}", ex.Message),
                NotificationSeverity.Error);
        }
    }

    private void MoveProjectCardToCommittedFolder(ProjectItemViewModel project)
    {
        foreach (ProjectFolderViewModel folder in ProjectFolders)
        {
            if (!folder.AllProjects.Contains(project))
                continue;

            folder.ReplaceProjects(
                folder.Projects.Where(candidate => !ReferenceEquals(candidate, project)),
                folder.AllProjects.Where(candidate => !ReferenceEquals(candidate, project)));
        }

        UngroupedProjects.Remove(project);

        ProjectFolderViewModel? destinationFolder = ProjectFolders.FirstOrDefault(folder =>
            string.Equals(folder.Id, project.GroupId, StringComparison.OrdinalIgnoreCase));
        if (destinationFolder is not null)
        {
            List<ProjectItemViewModel> visible = [.. SortProjectItems(destinationFolder.Projects.Append(project))];
            List<ProjectItemViewModel> all = [.. SortProjectItems(destinationFolder.AllProjects.Append(project))];
            destinationFolder.ReplaceProjects(visible, all);
            destinationFolder.IsExpanded = true;
        }
        else
        {
            List<ProjectItemViewModel> ungrouped = [.. SortProjectItems(UngroupedProjects.Append(project))];
            UngroupedProjects.SyncWith(ungrouped);
        }

        OnPropertiesChanged(nameof(HasProjectFolders), nameof(HasUngroupedProjects));
        RaiseProjectGroupCommandStates();
    }

    private void CreateProjectGroup()
    {
        try
        {
            SqliteRepository repo = CreateRepository(_configStore.GetSnapshot());
            repo.EnsureSchema();
            ProjectGroup group = repo.CreateProjectGroup(NewProjectGroupName);
            NewProjectGroupName = string.Empty;
            LoadGroupOptions();
            ApplyFilterAndSort(autoSelectIfNone: false);
            ShowNotification(
                Lf("Projects.Folder.Created", "Created folder “{0}”.", group.Name),
                NotificationSeverity.Info);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowNotification(ex.Message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowNotification(
                Lf("Projects.Folder.CreateFailed", "Could not create the folder: {0}", ex.Message),
                NotificationSeverity.Error);
        }
    }

    private static void BeginRenameProjectGroup(ProjectFolderViewModel? folder)
    {
        if (folder is not { CanManage: true })
            return;

        folder.EditName = folder.Name;
        folder.IsDeleteConfirmationVisible = false;
        folder.IsRenaming = true;
    }

    private static bool CanSaveRenameProjectGroup(ProjectFolderViewModel? folder) =>
        folder is { CanManage: true } &&
        !string.IsNullOrWhiteSpace(ProjectGroup.NormalizeName(folder.EditName));

    private void SaveRenameProjectGroup(ProjectFolderViewModel? folder)
    {
        if (!CanSaveRenameProjectGroup(folder))
            return;

        try
        {
            SqliteRepository repo = CreateRepository(_configStore.GetSnapshot());
            if (!repo.RenameProjectGroup(folder!.Id, folder.EditName))
                throw new InvalidOperationException(L("Projects.Folder.Missing", "That folder no longer exists."));

            string normalizedName = ProjectGroup.NormalizeName(folder.EditName);
            folder.Rename(normalizedName);
            LoadGroupOptions();
            ApplyFilterAndSort(autoSelectIfNone: false);
            ShowNotification(
                Lf("Projects.Folder.Renamed", "Renamed the folder to “{0}”.", normalizedName),
                NotificationSeverity.Info);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowNotification(ex.Message, NotificationSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowNotification(
                Lf("Projects.Folder.RenameFailed", "Could not rename the folder: {0}", ex.Message),
                NotificationSeverity.Error);
        }
    }

    private static void CancelRenameProjectGroup(ProjectFolderViewModel? folder)
    {
        if (folder is null)
            return;

        folder.EditName = folder.Name;
        folder.IsRenaming = false;
    }

    private static void RequestDeleteProjectGroup(ProjectFolderViewModel? folder)
    {
        if (folder is not { CanManage: true })
            return;

        folder.IsRenaming = false;
        folder.IsDeleteConfirmationVisible = true;
    }

    private void DeleteProjectGroup(ProjectFolderViewModel? folder)
    {
        if (folder is not { CanManage: true })
            return;

        try
        {
            SqliteRepository repo = CreateRepository(_configStore.GetSnapshot());
            if (!repo.DeleteProjectGroup(folder.Id))
                throw new InvalidOperationException(L("Projects.Folder.Missing", "That folder no longer exists."));

            Interlocked.Increment(ref _suppressProjectPersistence);
            try
            {
                foreach (ProjectItemViewModel project in _allProjects.Where(project =>
                             string.Equals(project.GroupId, folder.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    project.GroupId = ProjectGroupOption.UngroupedId;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _suppressProjectPersistence);
            }

            string removedName = folder.Name;
            LoadGroupOptions();
            ApplyFilterAndSort(autoSelectIfNone: false);
            ShowNotification(
                Lf("Projects.Folder.Deleted", "Deleted folder “{0}”. Its projects are now Ungrouped.", removedName),
                NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowNotification(
                Lf("Projects.Folder.DeleteFailed", "Could not delete the folder: {0}", ex.Message),
                NotificationSeverity.Error);
        }
    }

    private static void CancelDeleteProjectGroup(ProjectFolderViewModel? folder)
    {
        if (folder is not null)
            folder.IsDeleteConfirmationVisible = false;
    }

    private void RefreshGroupAutoBackupStateFromConfig(AppConfig? config = null)
    {
        config ??= _configStore.GetSnapshot();
        _autoBackupDisabledProjectIds = [.. config.Backups.AutoBackupDisabledProjects ?? []];
        foreach (ProjectFolderViewModel folder in ProjectFolders)
            folder.NotifyAggregateChanged();
        RaiseProjectGroupCommandStates();
    }

    private static List<int> GetProjectGroupRegisteredProjectIds(ProjectFolderViewModel? folder)
    {
        if (folder is null)
            return [];

        return [.. folder.AllProjects
            .Where(project => project.IsRegistered && project.ProjectId > 0)
            .Select(project => project.ProjectId)
            .Distinct()];
    }

    private static bool CanRunProjectGroupAction(ProjectFolderViewModel? folder) =>
        GetProjectGroupRegisteredProjectIds(folder).Count > 0;

    private bool CanSetProjectGroupAutoBackup(ProjectFolderViewModel? folder, bool enabled)
    {
        List<int> ids = GetProjectGroupRegisteredProjectIds(folder);
        return enabled
            ? ids.Any(_autoBackupDisabledProjectIds.Contains)
            : ids.Any(id => !_autoBackupDisabledProjectIds.Contains(id));
    }

    private void RaiseProjectGroupCommandStates()
    {
        _snapshotGroupCommand.RaiseCanExecuteChanged();
        _backupGroupCommand.RaiseCanExecuteChanged();
        _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
        _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
        _saveRenameProjectGroupCommand.RaiseCanExecuteChanged();
    }

    private async Task SnapshotProjectGroupAsync(ProjectFolderViewModel? folder)
    {
        if (folder is null)
            return;

        List<int> ids = GetProjectGroupRegisteredProjectIds(folder);
        if (ids.Count == 0)
            return;

        try
        {
            var config = await Task.Run(_configStore.GetSnapshot).ConfigureAwait(false);
            var maxSnapshotsToKeep = config.Backups.MaxSnapshotsPerProject;
            var fullHash = config.Backups.UseFullSnapshotHash;
            var enableScanCache = config.Backups.EnableScanCache;
            var aggressiveScanCache = config.Backups.AggressiveScanCache;
            List<ProjectItemViewModel> targets = [.. folder.AllProjects.Where(project => ids.Contains(project.ProjectId))];

            if (targets.Count == 0)
                return;

            var repo = CreateRepository(config);
            var existingByName = (await repo.GetAllProjectsAsync().ConfigureAwait(false))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var hashService = new HashService();
            var snapshotService = new SnapshotService(repo, hashService);

            var success = 0;
            var failure = 0;

            foreach (string targetName in targets.Select(target => target.Name))
            {
                if (!existingByName.TryGetValue(targetName, out var existing))
                    continue;

                try
                {
                    await snapshotService.CreateSnapshotAsync(
                        existing,
                        fullHash: fullHash,
                        hashNow: true,
                        maxSnapshotsToKeep: maxSnapshotsToKeep,
                        ct: CancellationToken.None,
                        progressCallback: null,
                        useScanCache: enableScanCache,
                        aggressiveScanCache: aggressiveScanCache).ConfigureAwait(false);
                    success++;
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Record($"Folder snapshot failed for '{targetName}': {ex.GetType().Name} - {ex.Message}");
                    failure++;
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (success > 0)
                {
                    ShowNotification(
                        Lf("Projects.Group.SnapshotSuccess", "Created snapshots for {0} projects.", success),
                        NotificationSeverity.Info);
                }

                if (failure > 0)
                {
                    ShowNotification(
                        Lf("Projects.Group.SnapshotFailure", "Failed to create snapshots for {0} projects.", failure),
                        NotificationSeverity.Warning);
                }
            });

            await RefreshAsync(forceDiscovery: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowNotification(
                    Lf("Projects.Group.SnapshotError", "Failed to run grouped snapshot operation: {0}", ex.Message),
                    NotificationSeverity.Error);
            });
        }
    }

    private async Task BackupProjectGroupAsync(ProjectFolderViewModel? folder)
    {
        List<int> ids = GetProjectGroupRegisteredProjectIds(folder);
        if (ids.Count == 0)
            return;

        BackupGroupRequested?.Invoke(ids);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowNotification(
                Lf("Projects.Group.BackupQueued", "Queued backup for {0} projects.", ids.Count),
                NotificationSeverity.Info);
        });
    }

    private async Task SetAutoBackupForProjectGroupAsync(ProjectFolderViewModel? folder, bool enabled)
    {
        List<int> ids = GetProjectGroupRegisteredProjectIds(folder);
        if (ids.Count == 0)
            return;

        await Task.Run(() =>
        {
            var cfg = _configStore.Load();
            var disabled = cfg.Backups.AutoBackupDisabledProjects ?? [];
            disabled = enabled
                ? [.. disabled.Except(ids).Distinct()]
                : [.. disabled.Concat(ids).Distinct()];
            cfg.Backups.AutoBackupDisabledProjects = disabled;
            _configStore.Save(cfg);
        }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (enabled)
                _autoBackupDisabledProjectIds.ExceptWith(ids);
            else
                _autoBackupDisabledProjectIds.UnionWith(ids);

            Interlocked.Increment(ref _suppressProjectPersistence);
            try
            {
                foreach (ProjectItemViewModel project in _allProjects.Where(project => ids.Contains(project.ProjectId)))
                    project.IsAutoBackupEnabled = enabled;
            }
            finally
            {
                Interlocked.Decrement(ref _suppressProjectPersistence);
            }

            ShowNotification(
                enabled
                    ? Lf("Projects.Group.AutoBackupEnabled", "Enabled auto backups for {0} projects.", ids.Count)
                    : Lf("Projects.Group.AutoBackupDisabled", "Disabled auto backups for {0} projects.", ids.Count),
                NotificationSeverity.Info);
            _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
            _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
            folder?.NotifyAggregateChanged();
            AutoBackupGroupPreferenceChanged?.Invoke(ids, enabled);
        });
    }
}
