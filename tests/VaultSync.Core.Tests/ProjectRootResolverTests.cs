using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectRootResolverTests : IDisposable
{
    private readonly TempDirectory _tempRoot = new();

    [Fact]
    public void TryResolveExistingProjectRoot_UsesLeafFromWindowsPathWhenProjectNameDiffers()
    {
        string projectsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot.Path, "Projects")).FullName;
        string projectRoot = Directory.CreateDirectory(Path.Combine(projectsRoot, "real-folder")).FullName;

        bool resolved = ProjectRootResolver.TryResolveExistingProjectRoot(
            projectsRoot,
            "Display Name",
            @"D:\Dev\real-folder",
            out string resolvedRoot);

        Assert.True(resolved);
        Assert.Equal(projectRoot, resolvedRoot);
    }

    [Fact]
    public void TryResolveExistingProjectRoot_MatchesProjectFolderIgnoringCase()
    {
        string projectsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot.Path, "Projects")).FullName;
        string projectRoot = Directory.CreateDirectory(Path.Combine(projectsRoot, "Blueprints")).FullName;

        bool resolved = ProjectRootResolver.TryResolveExistingProjectRoot(
            projectsRoot,
            "blueprints",
            null,
            out string resolvedRoot);

        Assert.True(resolved);
        Assert.Equal(projectRoot, resolvedRoot);
    }

    public void Dispose()
    {
        _tempRoot.Dispose();
    }
}
