using System;
using System.IO;
using System.Reflection;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectPresetSelectionTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();

    [Fact]
    public void RequiredPreset_UsesRecommendationBeforeGenericFallback()
    {
        var projectRoot = Path.Combine(_tempDir.Path, "UnityGame");
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
        var projectRoot = Path.Combine(_tempDir.Path, "PlainProject");
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
        _tempDir.Dispose();
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
