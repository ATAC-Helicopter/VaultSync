#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AutomaticMetadataImportTests
{
    [Fact]
    public async Task PeriodicPass_WithMaintenanceDisabled_ImportsLaterAvailableAndUpdatedSource_AndSkipsUnchangedSource()
    {
        int checkpoints = 0;
        using var fixture = new Fixture(_ => Interlocked.Increment(ref checkpoints));
        fixture.Invoke("ConfigureMaintenanceTimer");
        var timer = fixture.Field<Timer>("_maintenanceTimer");
        Assert.NotNull(timer);
        timer.Dispose();

        await fixture.Tick();
        Assert.False(Directory.Exists(fixture.Source));
        Assert.Empty(fixture.Repository.GetAllProjects());

        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "first");
        await fixture.Tick();
        Project first = Assert.Single(fixture.Repository.GetAllProjects());
        Assert.True(first.IsImported);
        Assert.Null(first.EncryptionKeyRef);
        int firstCheckpoints = checkpoints;
        Assert.True(firstCheckpoints > 0);

        fixture.ExpireThrottle();
        await fixture.Tick();
        Assert.Equal(firstCheckpoints, checkpoints);
        Assert.Single(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);

        fixture.AddProject(store, "second");
        fixture.ExpireThrottle();
        await fixture.Tick();
        Assert.Equal(2, fixture.Repository.GetAllProjects().Count());
        Assert.True(checkpoints > firstCheckpoints);
        Assert.Empty(fixture.InFlight);
        Assert.Empty(fixture.RetryAfter);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("global-sync")]
    [InlineData("global-import")]
    [InlineData("destination-sync")]
    [InlineData("destination-import")]
    public async Task PeriodicPass_RespectsImportOptOuts(string optOut)
    {
        using var fixture = new Fixture();
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "opt-out");
        switch (optOut)
        {
            case "inactive": fixture.Destination.Active = false; break;
            case "global-sync": fixture.Config.Backups.EnableMetadataSync = false; break;
            case "global-import": fixture.Config.Backups.AutoImportMetadata = false; break;
            case "destination-sync": fixture.Destination.EnableMetadataSync = false; break;
            case "destination-import": fixture.Destination.AutoImportMetadata = false; break;
        }
        fixture.SaveConfig();

        await fixture.Tick();

        Assert.Empty(fixture.Repository.GetAllProjects());
        Assert.Empty(fixture.Attempts);
        Assert.Empty(fixture.InFlight);
    }

    [Fact]
    public async Task PeriodicPass_MissingLocalPath_DoesNotCreateDirectoryOrChangeDestination()
    {
        using var fixture = new Fixture();
        fixture.Destination.AutoMount = true;
        fixture.Destination.CredentialName = "never-unlock";
        fixture.SaveConfig();

        await fixture.Tick();

        Assert.False(Directory.Exists(fixture.Source));
        Assert.False(fixture.Destination.PreMounted);
        BackupDestination saved = Assert.Single(AppConfigStore.Load().Backups.Destinations);
        Assert.False(saved.PreMounted);
        Assert.True(saved.AutoMount);
        Assert.Equal("never-unlock", saved.CredentialName);
        Assert.Empty(fixture.Attempts);
    }

    [Fact]
    public async Task AutomaticCalls_SharingSource_DoNotOverlap_AndReleaseSingleFlightGate()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Synthetic import was not released.");
        });
        fixture.AddProject(fixture.CreateStore(), "shared");
        Task first = fixture.Tick();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            Assert.Single(fixture.InFlight);
            // Bypass the timestamp guard to exercise the separate in-flight guard.
            fixture.ExpireThrottle();
            await fixture.InvokeTask("TryImportMetadataForDestination", fixture.Config, fixture.Destination, Path.Combine(fixture.Source, "."))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.Single(fixture.InFlight);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Empty(fixture.InFlight);
        Assert.Single(fixture.Repository.GetAllProjects());
        Assert.Empty(fixture.RetryAfter);
    }

    [Fact]
    public async Task ReadOnlyImport_CopyFailure_FailsClosed_AndCanRestartWithoutMutatingRepository()
    {
        string database = string.Empty;
        bool removeBeforeCopy = true;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == "metadata-import-read-copy" && removeBeforeCopy)
                File.Delete(database);
        });
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "copy-failure");
        database = store.DatabasePath;
        byte[] original = File.ReadAllBytes(database);
        MetadataSyncService service = fixture.Field<MetadataSyncService>("_metadataSyncService");

        MetadataSyncResult result = service.ImportFromStore(fixture.Source,
            MetadataSyncOptions.Default.AsReadOnlySource().WithUnchangedSourceSkip());

        Assert.Equal(MetadataSyncStatus.InvalidStore, result.Status);
        Assert.Equal("Could not copy metadata store for read-only import.", result.Message);
        Assert.Empty(fixture.Repository.GetAllProjects());
        Assert.Empty(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);

        removeBeforeCopy = false;
        File.WriteAllBytes(database, original);
        await fixture.Tick();
        Assert.Single(fixture.Repository.GetAllProjects());
        Assert.Empty(fixture.InFlight);
        Assert.Empty(fixture.RetryAfter);
    }

    [Fact]
    public void PreviewThenManualImport_WithoutSourceWrites_PreservesSourceFilesWhileApplyingReviewedLocalTombstone()
    {
        using var fixture = new Fixture();
        MetadataStore store = fixture.CreateStore();
        int projectId = TestRepository.AddProject(fixture.Repository, "reviewed-local", fixture.Root);
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int snapshotId = fixture.Repository.CreateSnapshotFromMetadata("reviewed-snapshot", projectId, created, 1, 8);
        string localBackupRoot = Path.Combine(fixture.Root, "local-backups");
        string relative = "reviewed-local/2026-01-01_00-00-00";
        string backupDirectory = Directory.CreateDirectory(Path.Combine(localBackupRoot, relative)).FullName;
        string localPayload = Path.Combine(backupDirectory, "payload.txt");
        File.WriteAllText(localPayload, "keep local payload");
        fixture.Repository.CreateBackupFromMetadata(
            "reviewed-backup", projectId, snapshotId, created, "manual", 8, relative,
            localBackupRoot, "Local", isProtected: false, isImported: false);
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "backup", EntityId = "reviewed-backup", DeletedUtc = created.AddDays(1), OriginMachineId = "remote"
        });
        string[] sourceFiles = Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(path => path).ToArray();
        var sourceBytes = sourceFiles.ToDictionary(path => path, File.ReadAllBytes);
        MetadataSyncService service = fixture.Field<MetadataSyncService>("_metadataSyncService");

        byte[] configBefore = File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json"));

        MetadataSyncPreview preview = service.PreviewImportFromStore(fixture.Source,
            MetadataSyncOptions.Default.WithoutSourceWrites());

        Assert.Equal(MetadataSyncStatus.Success, preview.Status);
        Assert.Equal(1, preview.DeletedBackups);
        Assert.NotNull(fixture.Repository.GetBackupByExternalId("reviewed-backup"));
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json")));
        Assert.Equal(sourceFiles, Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(path => path));
        Assert.All(sourceBytes, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));

        MetadataSyncResult result = service.ImportFromStore(fixture.Source,
            MetadataSyncOptions.Default.WithoutSourceWrites());

        Assert.Equal(MetadataSyncStatus.Success, result.Status);
        Assert.Equal(1, result.AppliedTombstones);
        Assert.Null(fixture.Repository.GetBackupByExternalId("reviewed-backup"));
        Assert.NotNull(fixture.Repository.GetProjectById(projectId));
        Assert.True(File.Exists(localPayload));
        Assert.Equal(sourceFiles, Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(path => path));
        Assert.All(sourceBytes, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public void Preview_StrictCopyFailure_DoesNotMutateLocalRepositoryOrConfiguration_AndCanRestart()
    {
        string database = string.Empty;
        bool removeRequiredWal = true;
        using var fixture = new Fixture(checkpoint =>
        {
            if (removeRequiredWal && checkpoint == "metadata-import-read-copy-observed")
                File.Delete(database + "-wal");
        });
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "preview-remote");
        database = store.DatabasePath;
        byte[] originalDatabase = File.ReadAllBytes(database);
        File.WriteAllBytes(database + "-wal", new byte[128]);
        int localId = TestRepository.AddProject(fixture.Repository, "preview-local", fixture.Root);
        byte[] configBefore = File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json"));
        MetadataSyncService service = fixture.Field<MetadataSyncService>("_metadataSyncService");

        MetadataSyncPreview refused = service.PreviewImportFromStore(fixture.Source);

        Assert.Equal(MetadataSyncStatus.InvalidStore, refused.Status);
        Assert.Equal("Could not copy metadata store for read-only import.", refused.Message);
        Assert.Equal(localId, Assert.Single(fixture.Repository.GetAllProjects()).Id);
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json")));
        Assert.Empty(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);
        Assert.Equal(originalDatabase, File.ReadAllBytes(database));
        Assert.Equal(new[] { database }, Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories));

        removeRequiredWal = false;
        MetadataSyncPreview restarted = service.PreviewImportFromStore(fixture.Source);
        Assert.Equal(MetadataSyncStatus.Success, restarted.Status);
        Assert.Equal(1, restarted.NewProjects);
        Assert.Equal(localId, Assert.Single(fixture.Repository.GetAllProjects()).Id);
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json")));
        Assert.Equal(originalDatabase, File.ReadAllBytes(database));
        Assert.Equal(new[] { database }, Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReadOnlyCopy_WorkspaceIsPrivate_AndOmitsSharedMemoryIndex()
    {
        using var fixture = new Fixture();
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "private-copy");
        string sharedMemory = store.DatabasePath + "-shm";
        File.WriteAllBytes(sharedMemory, new byte[128]);
        byte[] databaseBefore = File.ReadAllBytes(store.DatabasePath);
        byte[] sharedMemoryBefore = File.ReadAllBytes(sharedMemory);
        object?[] arguments = [store.DatabasePath, string.Empty, true, null];

        bool success = (bool)typeof(MetadataSyncService)
            .GetMethod("TryCopyStoreForRead", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;

        Assert.True(success);
        using var workspace = new TempDirectory((string)arguments[1]!);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(workspace.Path));
        string copiedDatabase = Path.Combine(workspace.Path, ".vaultsync", "meta", Path.GetFileName(store.DatabasePath));
        Assert.Equal(databaseBefore, File.ReadAllBytes(copiedDatabase));
        Assert.False(File.Exists(copiedDatabase + "-shm"));
        Assert.Equal(databaseBefore, File.ReadAllBytes(store.DatabasePath));
        Assert.Equal(sharedMemoryBefore, File.ReadAllBytes(sharedMemory));
    }

    [Theory]
    [InlineData("missing-wal")]
    [InlineData("blocked-journal")]
    [InlineData("appearing-wal")]
    [InlineData("changed-wal-header")]
    [InlineData("growing-database")]
    [InlineData("growing-wal")]
    public void ReadOnlyImport_StrictCopyFault_RefusesPartialSnapshotWithoutLocalMutation_AndCanRestart(string fault)
    {
        string database = string.Empty;
        bool injectFault = true;
        bool faultInjected = false;
        var expectedSource = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var fixture = new Fixture(checkpoint =>
        {
            string boundary = fault is "changed-wal-header" or "growing-wal"
                ? "metadata-import-read-copy-copied"
                : "metadata-import-read-copy-observed";
            if (!injectFault || checkpoint != boundary)
                return;

            faultInjected = true;
            string sidecar = database + (fault == "blocked-journal" ? "-journal" : "-wal");
            switch (fault)
            {
                case "missing-wal":
                    File.Delete(sidecar);
                    expectedSource.Remove(sidecar);
                    break;
                case "blocked-journal":
                    File.Delete(sidecar);
                    Directory.CreateDirectory(sidecar);
                    expectedSource.Remove(sidecar);
                    break;
                case "appearing-wal":
                    File.WriteAllBytes(sidecar, new byte[128]);
                    expectedSource[sidecar] = File.ReadAllBytes(sidecar);
                    break;
                case "changed-wal-header":
                    DateTime originalWrite = File.GetLastWriteTimeUtc(sidecar);
                    byte[] changedHeader = File.ReadAllBytes(sidecar);
                    changedHeader[0] ^= 1;
                    File.WriteAllBytes(sidecar, changedHeader);
                    // Keep length/time unchanged: the bounded header check must refuse.
                    File.SetLastWriteTimeUtc(sidecar, originalWrite);
                    expectedSource[sidecar] = changedHeader;
                    break;
                case "growing-database":
                case "growing-wal":
                    string growingPath = fault == "growing-database" ? database : sidecar;
                    using (var growing = new FileStream(growingPath, FileMode.Append, FileAccess.Write))
                        growing.Write(new byte[128]);
                    expectedSource[growingPath] = File.ReadAllBytes(growingPath);
                    break;
            }
        });
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "strict-copy-remote");
        database = store.DatabasePath;
        byte[] originalDatabase = File.ReadAllBytes(database);
        if (fault is "missing-wal" or "changed-wal-header" or "growing-wal")
            File.WriteAllBytes(database + "-wal", new byte[128]);
        if (fault == "blocked-journal")
            File.WriteAllBytes(database + "-journal", new byte[128]);
        foreach (string path in Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories))
            expectedSource[path] = File.ReadAllBytes(path);
        int localId = TestRepository.AddProject(fixture.Repository, "local-only", fixture.Root);
        byte[] configBefore = File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json"));
        MetadataSyncService service = fixture.Field<MetadataSyncService>("_metadataSyncService");

        MetadataSyncResult result = service.ImportFromStore(fixture.Source,
            MetadataSyncOptions.Default.AsReadOnlySource().WithUnchangedSourceSkip());

        Assert.True(faultInjected);
        Assert.Equal(MetadataSyncStatus.InvalidStore, result.Status);
        Assert.Equal("Could not copy metadata store for read-only import.", result.Message);
        Project local = Assert.Single(fixture.Repository.GetAllProjects());
        Assert.Equal(localId, local.Id);
        Assert.Equal("local-only", local.Name);
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.ConfigDirectory, "appsettings.json")));
        Assert.Empty(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);
        Assert.Equal(expectedSource.Keys.OrderBy(path => path),
            Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(path => path));
        Assert.All(expectedSource, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));

        injectFault = false;
        foreach (string suffix in new[] { "-wal", "-journal" })
        {
            string path = database + suffix;
            if (Directory.Exists(path))
                Directory.Delete(path);
            else if (File.Exists(path))
                File.Delete(path);
        }
        File.WriteAllBytes(database, originalDatabase);
        MetadataSyncResult restarted = service.ImportFromStore(fixture.Source,
            MetadataSyncOptions.Default.AsReadOnlySource().WithUnchangedSourceSkip());
        Assert.Equal(MetadataSyncStatus.Success, restarted.Status);
        Assert.Equal(2, fixture.Repository.GetAllProjects().Count());
        Assert.NotNull(fixture.Repository.GetProjectById(localId));
        Assert.Single(AppConfigStore.Load().Advanced.MetadataImportCache.Sources);
    }

    [Fact]
    public async Task AutomaticImport_PreservesSourceFilesAndLocalFields_WithoutRetention_WhileExplicitRetentionStillWorks()
    {
        var checkpoints = new ConcurrentQueue<string>();
        using var fixture = new Fixture(checkpoints.Enqueue);
        MetadataStore store = fixture.CreateStore();
        fixture.AddProject(store, "history");
        string localSource = Directory.CreateDirectory(Path.Combine(fixture.Root, "local-project")).FullName;
        int id = fixture.Repository.AddProject(new Project
        {
            ExternalId = "history",
            Name = "history",
            RootPath = localSource,
            Preset = "generic",
            EncryptionKeyRef = "local-key-only"
        });
        for (int index = 0; index < 2; index++)
        {
            string relative = $"history/2026-01-0{index + 1}_00-00-00";
            string folder = Directory.CreateDirectory(Path.Combine(fixture.Source, relative)).FullName;
            File.WriteAllText(Path.Combine(folder, "payload.txt"), $"backup {index}");
            store.UpsertSnapshot(new MetaSnapshot
            {
                ExternalId = $"snapshot-{index}", ProjectExternalId = "history",
                CreatedUtc = DateTime.UtcNow.AddDays(-2 + index), FileCount = 1, TotalBytes = 8
            });
            store.UpsertBackup(new MetaBackup
            {
                ExternalId = $"backup-{index}", ProjectExternalId = "history", SnapshotExternalId = $"snapshot-{index}",
                CreatedUtc = DateTime.UtcNow.AddDays(-2 + index), Type = "manual", TotalBytes = 8,
                PathRel = relative, DestinationAlias = "Primary", KdfParamsJson = "{}"
            });
        }
        int existingSnapshot = fixture.Repository.CreateSnapshotFromMetadata(
            "snapshot-0", id, DateTime.UtcNow.AddDays(-2), 1, 8);
        fixture.Repository.CreateBackupFromMetadata(
            "backup-0", id, existingSnapshot, DateTime.UtcNow.AddDays(-2), "manual", 8,
            "history/2026-01-01_00-00-00", fixture.Source, "Primary", isProtected: false, isImported: false);
        store.AddTombstone(new MetaTombstone
        {
            EntityType = "backup", EntityId = "backup-0", DeletedUtc = DateTime.UtcNow, OriginMachineId = "remote"
        });
        string[] sourceFiles = Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        var sourceBytes = sourceFiles.ToDictionary(p => p, File.ReadAllBytes);

        await fixture.Tick();

        Project local = fixture.Repository.GetProjectById(id)!;
        Assert.Equal(localSource, local.RootPath);
        Assert.Equal("local-key-only", local.EncryptionKeyRef);
        string[] backupIds = fixture.Repository.GetBackupsForProject(id)
            .Select(backup => backup.ExternalId).OrderBy(externalId => externalId, StringComparer.Ordinal).ToArray();
        AppConfig observedConfig = AppConfigStore.GetSnapshot();
        string importDiagnostics = $"Surviving backup IDs: [{string.Join(", ", backupIds)}]; " +
            $"checkpoints: [{string.Join(", ", checkpoints)}]; " +
            $"attempted paths: [{string.Join(", ", fixture.Attempts.Keys)}]; " +
            $"retry paths: [{string.Join(", ", fixture.RetryAfter.Keys)}]; " +
            $"fixture config active: {observedConfig.DbPath == fixture.Config.DbPath}; " +
            $"global sync/import: {observedConfig.Backups.EnableMetadataSync}/{observedConfig.Backups.AutoImportMetadata}.";
        Assert.True(fixture.RetryAfter.IsEmpty, importDiagnostics);
        Assert.True(new[] { "backup-0", "backup-1" }.SequenceEqual(backupIds), importDiagnostics);
        Assert.All(sourceBytes, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.Equal(sourceFiles, Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray());
        Assert.All(sourceFiles.Where(p => p.EndsWith("payload.txt", StringComparison.Ordinal)), p => Assert.True(File.Exists(p)));
        Assert.Empty(fixture.RetryAfter);

        // Prove the real retention service is wired and would prune this fixture
        // when called explicitly; automatic importing must never call it.
        fixture.Invoke("ApplyRetentionAfterMetadataImport", fixture.Source,
            new MetadataSyncResult(MetadataSyncStatus.Success, 0, 0, 2, 0, "fixture") { AffectedProjectIds = [id] });
        Assert.Single(fixture.Repository.GetBackupsForProject(id));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _root = new();
        private readonly TestAppConfigScope _configScope = new();
        private readonly AppViewModel _viewModel = (AppViewModel)RuntimeHelpers.GetUninitializedObject(typeof(AppViewModel));
        public string Root => _root.Path;
        public string ConfigDirectory => _configScope.ConfigDirectory;
        public string Source => Path.Combine(Root, "destination");
        public AppConfig Config { get; }
        public BackupDestination Destination { get; }
        public SqliteRepository Repository { get; }
        public ConcurrentDictionary<string, DateTime> Attempts => Field<ConcurrentDictionary<string, DateTime>>("_metadataImportAttempts");
        public ConcurrentDictionary<string, DateTime> RetryAfter => Field<ConcurrentDictionary<string, DateTime>>("_metadataImportRetryAfter");
        public ConcurrentDictionary<string, byte> InFlight => Field<ConcurrentDictionary<string, byte>>("_metadataImportsInFlight");

        public Fixture(Action<string>? checkpoint = null)
        {
            Destination = new BackupDestination { Path = Source, Alias = "Primary" };
            Config = new AppConfig { DbPath = Path.Combine(Root, "local.db"), ProjectsRoot = Path.Combine(Root, "local-root") };
            Config.Backups.UseAdvancedDestinations = true;
            Config.Backups.Destinations = [Destination];
            Config.Backups.EnableMetadataSync = true;
            Config.Backups.AutoImportMetadata = true;
            Config.Backups.MaxSnapshotsPerProject = 1;
            Config.Advanced.Maintenance.Enabled = false;
            SaveConfig();
            Repository = TestRepository.Create(Config.DbPath);
            Set("_configStore", StaticAppConfigStore.Instance);
            Set("_networkMountService", new NetworkMountService(_ => throw new InvalidOperationException("Credential access is forbidden.")));
            Set("_metadataImportAttempts", new ConcurrentDictionary<string, DateTime>());
            Set("_metadataImportRetryAfter", new ConcurrentDictionary<string, DateTime>());
            Set("_metadataImportsInFlight", new ConcurrentDictionary<string, byte>());
            Set("_repo", Repository);
            Set("_backupService", new BackupService(Repository));
            Set("_metadataSyncService", new MetadataSyncService(Repository, null, null, null, null, null, checkpoint));
        }

        public void SaveConfig() => AppConfigStore.Save(Config);
        public Task Tick() => InvokeTask("SafeRunScheduledMaintenanceAsync");
        public void ExpireThrottle() => Attempts[Source] = DateTime.UtcNow.AddMinutes(-6);
        public Task InvokeTask(string method, params object[] arguments) => (Task)Invoke(method, arguments)!;
        public object? Invoke(string method, params object[] arguments) => typeof(AppViewModel)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_viewModel, arguments);
        public T Field<T>(string name) => (T)typeof(AppViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_viewModel)!;
        private void Set(string name, object value) => typeof(AppViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_viewModel, value);

        public MetadataStore CreateStore()
        {
            var store = new MetadataStore(Source);
            store.EnsureSchema();
            store.UpsertMetaInfo(new MetaInfo
            {
                SchemaVersion = MetadataStore.CurrentSchemaVersion, CreatedUtc = DateTime.UtcNow,
                LastWriteUtc = DateTime.UtcNow, WriterAppVersion = "1.9.0", WriterMachineId = "remote"
            });
            return store;
        }

        public void AddProject(MetadataStore store, string externalId) => store.UpsertProject(new MetaProject
        {
            ExternalId = externalId, Name = externalId, Preset = "generic", RootPathHint = "/remote-machine/project",
            CreatedUtc = DateTime.UtcNow.AddDays(-2), UpdatedUtc = DateTime.UtcNow,
            SettingsJson = "{\"encryptionKeyRef\":\"remote-key-never-import\"}"
        });

        public void Dispose()
        {
            Field<Timer?>("_maintenanceTimer")?.Dispose();
            _configScope.Dispose();
            _root.Dispose();
        }
    }
}
