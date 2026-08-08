using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class ProjectFolderViewModel : ViewModelBase
{
    public const string UngroupedId = "";

    private bool _isExpanded = true;
    private bool _isRenaming;
    private bool _isDeleteConfirmationVisible;
    private string _editName;

    public ProjectFolderViewModel(ProjectGroup? group, string ungroupedName)
    {
        Id = group?.Id ?? UngroupedId;
        Name = group?.Name ?? ungroupedName;
        SortOrder = group?.SortOrder ?? int.MaxValue;
        IsUngrouped = group is null;
        _editName = Name;
    }

    public string Id { get; }
    public string Name { get; private set; }
    public int SortOrder { get; }
    public bool IsUngrouped { get; }
    public bool CanManage => !IsUngrouped;
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public IReadOnlyList<ProjectItemViewModel> AllProjects { get; private set; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetField(ref _isRenaming, value))
                OnPropertyChanged(nameof(ShowBatchActions));
        }
    }

    public bool IsDeleteConfirmationVisible
    {
        get => _isDeleteConfirmationVisible;
        set => SetField(ref _isDeleteConfirmationVisible, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetField(ref _editName, value ?? string.Empty);
    }

    public int ProjectCount => AllProjects.Count;
    public int RegisteredProjectCount => AllProjects.Count(project => project.IsRegistered);
    public int HealthyProjectCount => AllProjects.Count(project => project.IsRegistered && project.Health == ProjectHealthStatus.Healthy);
    public int AttentionProjectCount => Math.Max(0, RegisteredProjectCount - HealthyProjectCount);
    public int PausedProjectCount => AllProjects.Count(project => project.IsRegistered && !project.IsAutoBackupEnabled);
    public bool HasProjects => Projects.Count > 0;
    public bool CanRunBatchActions => RegisteredProjectCount > 0;
    public bool ShowBatchActions => CanRunBatchActions && !IsRenaming;

    public string Summary
    {
        get
        {
            string format = LocalizationProvider.Service?.GetString("Projects.Folder.Summary")
                ?? "{0} project(s) · {1} healthy · {2} need attention";
            return string.Format(
                CultureInfo.CurrentCulture,
                format,
                ProjectCount,
                HealthyProjectCount,
                AttentionProjectCount);
        }
    }

    public string DeleteExplanation
    {
        get
        {
            string format = LocalizationProvider.Service?.GetString("Projects.Folder.DeleteExplanation")
                ?? "Delete the folder “{0}”? Its {1} project(s) will move to Ungrouped. No project, source file, snapshot, or backup will be deleted.";
            return string.Format(CultureInfo.CurrentCulture, format, Name, ProjectCount);
        }
    }

    public void ReplaceProjects(
        IEnumerable<ProjectItemViewModel> visibleProjects,
        IEnumerable<ProjectItemViewModel>? allProjects = null)
    {
        Projects.Clear();
        foreach (ProjectItemViewModel project in visibleProjects)
            Projects.Add(project);

        AllProjects = [.. allProjects ?? Projects];

        NotifyAggregateChanged();
    }

    public void Rename(string name)
    {
        Name = name;
        EditName = name;
        IsRenaming = false;
        OnPropertiesChanged(nameof(Name), nameof(DeleteExplanation));
    }

    public void NotifyAggregateChanged()
    {
        OnPropertiesChanged(
            nameof(ProjectCount),
            nameof(RegisteredProjectCount),
            nameof(HealthyProjectCount),
            nameof(AttentionProjectCount),
            nameof(PausedProjectCount),
            nameof(HasProjects),
            nameof(CanRunBatchActions),
            nameof(ShowBatchActions),
            nameof(Summary),
            nameof(DeleteExplanation));
    }
}
