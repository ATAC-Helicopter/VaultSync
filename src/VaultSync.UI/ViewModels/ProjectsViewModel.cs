using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Core.Repositories;
using VaultSync.UI.Services;
using System;
using System.Collections.Generic;
using VaultSync.Core.Models;

namespace VaultSync.UI.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    private readonly SqliteRepository _repo;
    private readonly UiEventBus _bus;

    public ObservableCollection<string> Projects { get; } = new();

    [ObservableProperty] private string? selectedProject;
    [ObservableProperty] private string? newProjectName;
    [ObservableProperty] private string? newProjectPath;
    [ObservableProperty] private string selectedPreset = "custom";
    public IReadOnlyList<string> Presets { get; } = new[] { "custom", "unity", "dotnet" };

    public ProjectsViewModel(SqliteRepository repo, UiEventBus bus)
    {
        _repo = repo;
        _bus = bus;

        _repo.EnsureSchema();
        Refresh();
    }

    [RelayCommand]
    private void AddProject()
{
    if (string.IsNullOrWhiteSpace(NewProjectName) || string.IsNullOrWhiteSpace(NewProjectPath))
    {
        _bus.Warn("Enter name and path to add a project.");
        return;
    }
    var p = new Project
    {
        Name = NewProjectName!,
        RootPath = NewProjectPath!,
        Preset = SelectedPreset
    };
    _repo.AddProject(p);
    _bus.Success($"Added project '{p.Name}'.");
    Refresh();
}

    [RelayCommand]
    private void RemoveSelected()
{
    if (string.IsNullOrWhiteSpace(SelectedProject))
    {
        _bus.Warn("Select a project to remove.");
        return;
    }
    _repo.DeleteProjectCascade(SelectedProject!);
    _bus.Success($"Removed '{SelectedProject}'.");
    Refresh();
}

    [RelayCommand]
    private void SetPath()
{
    if (string.IsNullOrWhiteSpace(SelectedProject) || string.IsNullOrWhiteSpace(NewProjectPath))
    {
        _bus.Warn("Select a project and enter a new path.");
        return;
    }
    _repo.UpdateProjectPath(SelectedProject!, NewProjectPath!, out var oldPath);
    _bus.Success($"Updated path for '{SelectedProject}' to '{NewProjectPath}'.");
    Refresh();
}

    [RelayCommand]
    private void Refresh()
    {
        Projects.Clear();
        foreach (var p in _repo.ListProjects())
            Projects.Add(p.Name);
        _bus.Info($"Loaded {Projects.Count} project(s).");
    }
}