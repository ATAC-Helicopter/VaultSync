using System;
using System.IO;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectRootResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-root-resolver-{Guid.NewGuid():N}");

    [Fact]
    public void TryResolveExistingProjectRoot_UsesLeafFromWindowsPathWhenProjectNameDiffers()
    {
        string projectsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "Projects")).FullName;
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
        string projectsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "Projects")).FullName;
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
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
