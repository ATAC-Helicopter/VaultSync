using System;
using System.IO;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectGroupRepositoryTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly SqliteRepository _repo;

    public ProjectGroupRepositoryTests()
    {
        _repo = TestRepository.Create(Path.Combine(_tempDir.Path, "vaultsync.db"));
    }

    [Fact]
    public void CreateProjectGroup_NormalizesNameAndRejectsCaseInsensitiveDuplicate()
    {
        ProjectGroup group = _repo.CreateProjectGroup("  Client\t Work  ");

        Assert.Equal("Client Work", group.Name);
        ProjectGroup stored = Assert.Single(_repo.GetProjectGroups());
        Assert.Equal(group.Id, stored.Id);
        Assert.Equal("Client Work", stored.Name);

        Assert.Throws<InvalidOperationException>(() => _repo.CreateProjectGroup("client work"));
    }

    [Fact]
    public void SetProjectGroup_PersistsAndCanReturnProjectToUngrouped()
    {
        int projectId = TestRepository.AddProject(_repo, "Project", Path.Combine(_tempDir.Path, "Project"));
        ProjectGroup group = _repo.CreateProjectGroup("Work");

        Assert.True(_repo.SetProjectGroup(projectId, group.Id));
        Assert.Equal(group.Id, _repo.GetProjectById(projectId)!.GroupId);

        Assert.True(_repo.SetProjectGroup(projectId, null));
        Assert.Null(_repo.GetProjectById(projectId)!.GroupId);
    }

    [Fact]
    public void DeleteProjectGroup_UngroupsProjectsWithoutDeletingThem()
    {
        int projectId = TestRepository.AddProject(_repo, "Project", Path.Combine(_tempDir.Path, "Project"));
        ProjectGroup group = _repo.CreateProjectGroup("Archive");
        _repo.SetProjectGroup(projectId, group.Id);

        Assert.True(_repo.DeleteProjectGroup(group.Id));

        Assert.Empty(_repo.GetProjectGroups());
        Project project = Assert.Single(_repo.GetAllProjects());
        Assert.Equal(projectId, project.Id);
        Assert.Null(project.GroupId);
    }

    [Fact]
    public void SetProjectGroup_RejectsMissingGroupWithoutChangingMembership()
    {
        int projectId = TestRepository.AddProject(_repo, "Project", Path.Combine(_tempDir.Path, "Project"));

        Assert.Throws<InvalidOperationException>(() => _repo.SetProjectGroup(projectId, "missing"));
        Assert.Null(_repo.GetProjectById(projectId)!.GroupId);
    }

    [Fact]
    public void EnsureSchema_MigratesLegacyProjectsWithoutDataLoss()
    {
        string legacyPath = Path.Combine(_tempDir.Path, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            connection.Open();
            connection.ExecuteNonQuery("""
                CREATE TABLE projects(
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL UNIQUE,
                  root_path TEXT NOT NULL,
                  preset TEXT NOT NULL,
                  created_utc TEXT NOT NULL
                );
                INSERT INTO projects(name, root_path, preset, created_utc)
                VALUES('Legacy', '/legacy', '', '2026-01-01 00:00:00Z');
                """);
        }

        var migrated = new SqliteRepository(legacyPath);
        migrated.EnsureSchema();

        Assert.Equal("Legacy", Assert.Single(migrated.GetAllProjects()).Name);
        Assert.Empty(migrated.GetProjectGroups());
        ProjectGroup group = migrated.CreateProjectGroup("Migrated");
        Assert.True(migrated.SetProjectGroup(1, group.Id));
    }

    public void Dispose() => _tempDir.Dispose();
}

internal static class SqliteConnectionProjectGroupTestExtensions
{
    public static void ExecuteNonQuery(this SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
