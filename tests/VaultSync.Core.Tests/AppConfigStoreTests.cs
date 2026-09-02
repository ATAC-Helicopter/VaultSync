using System;
using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AppConfigStoreTests
{
    [Fact]
    public void Load_MissingScopedConfigKeepsDefaultDatabaseInsideScope()
    {
        using var scope = new TestAppConfigScope();

        AppConfig config = AppConfigStore.Load();

        Assert.Equal(Path.Combine(scope.ConfigDirectory, "vaultsync.db"), config.DbPath);
        Assert.True(File.Exists(Path.Combine(scope.ConfigDirectory, "appsettings.json")));
    }

    [Fact]
    public void UseDirectoryForTests_IsolatesConfigPersistence()
    {
        using var scope = new TestAppConfigScope();
        var config = new AppConfig
        {
            ProjectsRoot = Path.Combine(scope.ConfigDirectory, "Projects"),
            DbPath = Path.Combine(scope.ConfigDirectory, "vaultsync.db")
        };

        AppConfigStore.Save(config);

        AppConfig reloaded = AppConfigStore.Load();
        Assert.Equal(config.ProjectsRoot, reloaded.ProjectsRoot);
        Assert.Equal(config.DbPath, reloaded.DbPath);
        Assert.True(File.Exists(Path.Combine(scope.ConfigDirectory, "appsettings.json")));
    }

    [Fact]
    public void Save_RestrictsUnixConfigAndBackupPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var scope = new TestAppConfigScope();
        string configPath = Path.Combine(scope.ConfigDirectory, "appsettings.json");
        string backupPath = Path.Combine(scope.ConfigDirectory, "appsettings.bak.json");

        AppConfigStore.Save(new AppConfig());
        AppConfigStore.Save(new AppConfig { ProjectsRoot = "updated" });

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(scope.ConfigDirectory));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(configPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(backupPath));
    }

    [Fact]
    public void PrivateDataPermissions_RestrictsExistingUnixDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        File.SetUnixFileMode(
            directory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        PrivateDataPermissions.EnsureDirectory(directory.Path);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory.Path));
    }

    [Fact]
    public void SaveLoad_RoundTripsMetadataImportCache()
    {
        using var scope = new TestAppConfigScope();
        var config = new AppConfig
        {
            ProjectsRoot = Path.Combine(scope.ConfigDirectory, "Projects"),
            DbPath = Path.Combine(scope.ConfigDirectory, "vaultsync.db")
        };
        config.Advanced.MetadataImportCache.Sources.Add(new MetadataImportSourceStamp
        {
            SourceKey = "destination:primary",
            SourcePath = "/backups/.vaultsync/meta",
            SourceMachineId = "desktop-01",
            StoreUpdatedUtc = "2026-05-21T08:30:00Z",
            StoreSchemaVersion = 1,
            StoreFileLengthBytes = 4096,
            StoreFileUpdatedUtc = "2026-05-21T08:31:00Z",
            StoreSidecarStamp = "none",
            ImportedUtc = "2026-05-21T08:35:00Z",
            ProjectCount = 2,
            SnapshotCount = 3,
            BackupCount = 4,
            TombstoneCount = 1
        });

        AppConfigStore.Save(config);

        AppConfig reloaded = AppConfigStore.Load();
        MetadataImportSourceStamp source = Assert.Single(reloaded.Advanced.MetadataImportCache.Sources);
        Assert.Equal("destination:primary", source.SourceKey);
        Assert.Equal("/backups/.vaultsync/meta", source.SourcePath);
        Assert.Equal("desktop-01", source.SourceMachineId);
        Assert.Equal("2026-05-21T08:30:00Z", source.StoreUpdatedUtc);
        Assert.Equal(1, source.StoreSchemaVersion);
        Assert.Equal(4096, source.StoreFileLengthBytes);
        Assert.Equal("2026-05-21T08:31:00Z", source.StoreFileUpdatedUtc);
        Assert.Equal("none", source.StoreSidecarStamp);
        Assert.Equal("2026-05-21T08:35:00Z", source.ImportedUtc);
        Assert.Equal(2, source.ProjectCount);
        Assert.Equal(3, source.SnapshotCount);
        Assert.Equal(4, source.BackupCount);
        Assert.Equal(1, source.TombstoneCount);
    }

    [Fact]
    public void SaveLoad_RoundTripsExplicitOffsiteDestinationClassification()
    {
        using var scope = new TestAppConfigScope();
        var config = new AppConfig();
        config.Backups.UseAdvancedDestinations = true;
        config.Backups.Destinations.Add(new BackupDestination
        {
            Alias = "Remote archive",
            Path = "smb://backup.example/archive",
            IsOffsite = true
        });

        AppConfigStore.Save(config);

        BackupDestination destination = Assert.Single(AppConfigStore.Load().Backups.Destinations);
        Assert.True(destination.IsOffsite);
    }

    [Fact]
    public void Save_PreservesMetadataImportCacheWhenPendingConfigHasNoCacheEntries()
    {
        using var scope = new TestAppConfigScope();
        string projectsRoot = Path.Combine(scope.ConfigDirectory, "Projects");
        string dbPath = Path.Combine(scope.ConfigDirectory, "vaultsync.db");
        var config = new AppConfig
        {
            ProjectsRoot = projectsRoot,
            DbPath = dbPath
        };
        config.Advanced.MetadataImportCache.Sources.Add(new MetadataImportSourceStamp
        {
            SourceKey = "destination:primary",
            SourcePath = "/backups/.vaultsync/meta",
            SourceMachineId = "desktop-01",
            StoreUpdatedUtc = "2026-05-21T08:30:00Z",
            StoreSchemaVersion = 1,
            StoreFileLengthBytes = 4096,
            StoreFileUpdatedUtc = "2026-05-21T08:31:00Z",
            StoreSidecarStamp = "none",
            ImportedUtc = "2026-05-21T08:35:00Z",
            ProjectCount = 2,
            SnapshotCount = 3,
            BackupCount = 4,
            TombstoneCount = 1
        });
        AppConfigStore.Save(config);

        AppConfigStore.Save(new AppConfig
        {
            ProjectsRoot = projectsRoot,
            DbPath = dbPath
        });

        MetadataImportSourceStamp source = Assert.Single(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);
        Assert.Equal("destination:primary", source.SourceKey);
        Assert.Equal(4, source.BackupCount);
    }

    [Fact]
    public void Load_UsesBackupConfigWhenPrimaryConfigIsCorrupt()
    {
        using var scope = new TestAppConfigScope();
        string projectsRoot = Path.Combine(scope.ConfigDirectory, "Projects");
        string dbPath = Path.Combine(scope.ConfigDirectory, "vaultsync.db");

        File.WriteAllText(Path.Combine(scope.ConfigDirectory, "appsettings.json"), "{not valid json");
        File.WriteAllText(
            Path.Combine(scope.ConfigDirectory, "appsettings.bak.json"),
            $$"""
            {
              "ProjectsRoot": "{{projectsRoot.Replace("\\", "\\\\")}}",
              "DbPath": "{{dbPath.Replace("\\", "\\\\")}}"
            }
            """);

        AppConfig reloaded = AppConfigStore.Load();

        Assert.Equal(projectsRoot, reloaded.ProjectsRoot);
        Assert.Equal(dbPath, reloaded.DbPath);
    }

    [Fact]
    public void ResolveDbPath_UsesConfiguredPathOrDefaultFallback()
    {
        using var scope = new TestAppConfigScope();
        string configuredPath = Path.Combine(scope.ConfigDirectory, "configured.db");

        Assert.Equal(configuredPath, AppConfigStore.ResolveDbPath(new AppConfig { DbPath = configuredPath }));
        Assert.Equal(AppConfigStore.GetDefaultDbPath(), AppConfigStore.ResolveDbPath(new AppConfig { DbPath = " " }));
    }
}
