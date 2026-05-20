using System;
using System.IO;
using System.Reflection;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectPresetSelectionTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectPresetSelectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-preset-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void RequiredPreset_UsesRecommendationBeforeGenericFallback()
    {
        var projectRoot = Path.Combine(_tempDir, "UnityGame");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));

        var vm = new ProjectsViewModel();
        var project = new ProjectItemViewModel
        {
            Path = projectRoot,
            Preset = "generic"
        };

        Assert.Equal("unity", ResolveRequiredPreset(vm, project));
    }

    [Fact]
    public void RequiredPreset_UsesGenericWhenNoRecommendationApplies()
    {
        var projectRoot = Path.Combine(_tempDir, "PlainProject");
        Directory.CreateDirectory(projectRoot);

        var vm = new ProjectsViewModel();
        var project = new ProjectItemViewModel
        {
            Path = projectRoot,
            Preset = string.Empty
        };

        Assert.Equal("generic", ResolveRequiredPreset(vm, project));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static string ResolveRequiredPreset(ProjectsViewModel vm, ProjectItemViewModel project)
    {
        var method = typeof(ProjectsViewModel).GetMethod(
            "ResolveRequiredPreset",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(vm, [project, null]));
    }
}
