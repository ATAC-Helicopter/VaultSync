using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AppConfigStoreTests
{
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
}
