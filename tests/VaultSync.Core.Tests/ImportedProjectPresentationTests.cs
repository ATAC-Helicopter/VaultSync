using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ImportedProjectPresentationTests
{
    [Fact]
    public void MissingImportedSourceDisablesSnapshotUntilTheFolderIsAvailable()
    {
        using var configScope = new TestAppConfigScope();
        var viewModel = new ProjectsViewModel();
        var project = new ProjectItemViewModel { Name = "Remote", IsImported = true, IsLocalSourceAvailable = false };
        viewModel.SelectedProject = project;
        Assert.False(viewModel.SnapshotCommand.CanExecute(null));
        project.IsLocalSourceAvailable = true;
        Assert.True(viewModel.SnapshotCommand.CanExecute(null));
    }

    [Fact]
    public void RootlessImportedProjectsRemainVisibleAndDistinct()
    {
        using var root = new TempDirectory();
        using var configScope = new TestAppConfigScope();
        var config = new AppConfig { DbPath = Path.Combine(root.Path, "vault.db"), ProjectsRoot = root.Path };
        var repo = TestRepository.Create(config.DbPath);
        repo.AddProject(new Project { Name = "Remote One", RootPath = "", Preset = "generic", IsImported = true });
        repo.AddProject(new Project { Name = "Remote Two", RootPath = "", Preset = "generic", IsImported = true });
        List<ProjectItemViewModel> items = BuildItems(config, []);
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.True(item.IsImported);
            Assert.True(item.IsRegistered);
            Assert.False(item.IsLocalSourceAvailable);
            Assert.False(string.IsNullOrWhiteSpace(item.SourcePathDisplay));
        });
        Assert.NotEqual(items[0].ProjectId, items[1].ProjectId);
    }

    [Fact]
    public void RestoreRetainsOriginAndMergesDiscoveryByPathDespiteDifferentFolderName()
    {
        using var root = new TempDirectory();
        using var configScope = new TestAppConfigScope();
        var config = new AppConfig { DbPath = Path.Combine(root.Path, "vault.db"), ProjectsRoot = root.Path };
        var repo = TestRepository.Create(config.DbPath);
        int id = repo.AddProject(new Project { Name = "Remote Project", RootPath = "", Preset = "generic", IsImported = true, NeedsRestore = true });
        string local = Directory.CreateDirectory(Path.Combine(root.Path, "Restored-folder")).FullName;
        Assert.True(repo.UpdateProjectPath("Remote Project", local, out _));
        repo.UpdateProjectNeedsRestore(id, false);
        List<ProjectItemViewModel> items = BuildItems(config, [new DiscoveredProject("Restored-folder", local, null, null)]);
        var item = Assert.Single(items);
        Assert.Equal("Remote Project", item.Name);
        Assert.Equal(id, item.ProjectId);
        Assert.True(item.IsImported);
        Assert.True(item.IsLocalSourceAvailable);
        Assert.Equal(local, item.SourcePathDisplay);
        Assert.False(repo.GetProjectById(id)!.NeedsRestore);
    }

    [Fact]
    public void AdditiveSchemaUpgradePreservesLocalProjectIdentityWithoutInferringImportFromRestoreFlag()
    {
        using var root = new TempDirectory();
        string dbPath = Path.Combine(root.Path, "vault.db");
        var repo = TestRepository.Create(dbPath);
        int id = repo.AddProject(new Project { Name = "Local", RootPath = root.Path, Preset = "generic", NeedsRestore = true });
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE projects DROP COLUMN is_imported";
            command.ExecuteNonQuery();
        }
        repo.EnsureSchema();
        var project = repo.GetProjectById(id)!;
        Assert.Equal("Local", project.Name);
        Assert.Equal(root.Path, project.RootPath);
        Assert.True(project.NeedsRestore);
        Assert.False(project.IsImported);
    }

    private static List<ProjectItemViewModel> BuildItems(AppConfig config, IReadOnlyList<DiscoveredProject> sources)
    {
        var viewModel = new ProjectsViewModel();
        var method = typeof(ProjectsViewModel).GetMethod("BuildProjectItems", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<List<ProjectItemViewModel>>(method.Invoke(viewModel, [config, sources]));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void HistoricalRecoveryRequiresImportedOnlyHistoryAndAnUnavailableSource(
        bool hasLocalBackup, bool sourceExists, bool expected)
    {
        using var root = new TempDirectory();
        var repo = TestRepository.Create(Path.Combine(root.Path, "vault.db"));
        string path = Path.Combine(root.Path, "Source");
        if (sourceExists) Directory.CreateDirectory(path);
        int id = TestRepository.AddProject(repo, "Historical", path);
        int snapshot = repo.CreateSnapshot(id, 0, 0);
        repo.CreateBackupFromMetadata("remote", id, snapshot, DateTime.UtcNow, "manual", 0, "remote/backup", "", "", false, true);
        if (hasLocalBackup) repo.CreateBackup(id, snapshot, "manual", 0, "local/backup", "", "");
        repo.ReconcileHistoricalImportedProjectOrigins();
        repo.ReconcileHistoricalImportedProjectOrigins();
        Assert.Equal(expected, repo.GetProjectById(id)!.IsImported);
    }
}
