using System;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Tests.TestSupport;

public static class TestRepository
{
    public static SqliteRepository Create(string dbPath)
    {
        var repo = new SqliteRepository(dbPath);
        repo.EnsureSchema();
        return repo;
    }

    public static int AddProject(
        SqliteRepository repo,
        string name,
        string rootPath,
        string preset = "dotnet",
        DateTime? createdUtc = null)
    {
        var project = new Project
        {
            Name = name,
            RootPath = rootPath,
            Preset = preset,
            CreatedUtc = createdUtc ?? DateTime.UtcNow
        };

        return repo.AddProject(project);
    }
}
