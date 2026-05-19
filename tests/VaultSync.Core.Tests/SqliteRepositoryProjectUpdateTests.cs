using System;
using System.IO;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SqliteRepositoryProjectUpdateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SqliteRepositoryProjectUpdateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-project-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
    }

    [Fact]
    public void UpdateProjectPreset_PersistsTrimmedPreset()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        int projectId = repo.AddProject(new Project
        {
            Name = "Project",
            RootPath = Path.Combine(_tempDir, "Project"),
            Preset = "dotnet"
        });

        repo.UpdateProjectPreset(projectId, " unity ");

        Project updated = repo.GetProjectByName("Project")!;
        Assert.NotNull(updated);
        Assert.Equal("unity", updated.Preset);
    }

    [Fact]
    public void UpdateProjectPreset_BlankPreset_PersistsNoPreset()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        int projectId = repo.AddProject(new Project
        {
            Name = "Project",
            RootPath = Path.Combine(_tempDir, "Project"),
            Preset = "dotnet"
        });

        repo.UpdateProjectPreset(projectId, " ");

        Project updated = repo.GetProjectByName("Project")!;
        Assert.NotNull(updated);
        Assert.Equal(string.Empty, updated.Preset);
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
}
