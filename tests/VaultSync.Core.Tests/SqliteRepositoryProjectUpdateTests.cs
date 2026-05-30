using System;
using System.IO;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SqliteRepositoryProjectUpdateTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly string _dbPath;

    public SqliteRepositoryProjectUpdateTests()
    {
        _dbPath = Path.Combine(_tempDir.Path, "vaultsync.db");
    }

    [Fact]
    public void UpdateProjectPreset_PersistsTrimmedPreset()
    {
        var repo = TestRepository.Create(_dbPath);
        int projectId = TestRepository.AddProject(repo, "Project", Path.Combine(_tempDir.Path, "Project"));

        repo.UpdateProjectPreset(projectId, " unity ");

        Project updated = repo.GetProjectByName("Project")!;
        Assert.NotNull(updated);
        Assert.Equal("unity", updated.Preset);
    }

    [Fact]
    public void UpdateProjectPreset_BlankPreset_PersistsNoPreset()
    {
        var repo = TestRepository.Create(_dbPath);
        int projectId = TestRepository.AddProject(repo, "Project", Path.Combine(_tempDir.Path, "Project"));

        repo.UpdateProjectPreset(projectId, " ");

        Project updated = repo.GetProjectByName("Project")!;
        Assert.NotNull(updated);
        Assert.Equal(string.Empty, updated.Preset);
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
