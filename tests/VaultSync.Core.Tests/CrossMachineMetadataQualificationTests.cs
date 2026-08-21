using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CrossMachineMetadataQualificationTests
{
    [Fact]
    public void TwoInstallations_ConvergeReviewResolveRestartAndUndoWithoutSilentLoss()
    {
        using var sharedRepository = new TempDirectory();
        using var machineAState = new TempDirectory();
        using var machineBState = new TempDirectory();
        using var machineAProjectRoot = new TempDirectory();
        using var machineBProjectRoot = new TempDirectory();

        var machineAConfig = new FileBackedConfigStore(machineAState.Path);
        var machineBConfig = new FileBackedConfigStore(machineBState.Path);
        SqliteRepository machineARepository = CreateRepository(machineAConfig.GetDefaultDbPath());
        SqliteRepository machineBRepository = CreateRepository(machineBConfig.GetDefaultDbPath());
        const string externalId = "qualified-two-machine-project";
        int machineAProjectId = AddProject(machineARepository, externalId, machineAProjectRoot.Path);
        int machineBProjectId = AddProject(machineBRepository, externalId, machineBProjectRoot.Path);
        MetadataSyncService machineA = CreateSync(machineARepository, machineAConfig, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        MetadataSyncService machineB = CreateSync(machineBRepository, machineBConfig, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        AssertSuccess(machineA.ExportProjectToStore(
            sharedRepository.Path,
            machineAProjectId,
            "1.8.7",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        AssertSuccess(machineB.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));

        machineARepository.UpdateProjectTags(machineAProjectId, "edited-on-a");
        machineBRepository.UpdateProjectRestoreMode(machineBProjectId, ProjectRestoreMode.Sandbox);
        AssertSuccess(machineB.ExportProjectToStore(
            sharedRepository.Path,
            machineBProjectId,
            "1.8.7",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        AssertSuccess(machineA.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));
        Project mergedOnA = Assert.IsType<Project>(machineARepository.GetProjectById(machineAProjectId));
        Assert.Equal("edited-on-a", mergedOnA.Tags);
        Assert.Equal(ProjectRestoreMode.Sandbox, mergedOnA.RestoreMode);
        Assert.Empty(machineAConfig.Load().Advanced.ProjectMetadataConflicts);

        AssertSuccess(machineA.ExportProjectToStore(
            sharedRepository.Path,
            machineAProjectId,
            "1.8.7",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        AssertSuccess(machineB.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));
        Project convergedOnB = Assert.IsType<Project>(machineBRepository.GetProjectById(machineBProjectId));
        Assert.Equal("edited-on-a", convergedOnB.Tags);
        Assert.Equal(ProjectRestoreMode.Sandbox, convergedOnB.RestoreMode);

        machineARepository.UpdateProjectTags(machineAProjectId, "conflict-from-a");
        machineBRepository.UpdateProjectTags(machineBProjectId, "conflict-from-b");
        AssertSuccess(machineB.ExportProjectToStore(
            sharedRepository.Path,
            machineBProjectId,
            "1.8.7",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        AssertSuccess(machineA.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));

        Project retainedBeforeReview = Assert.IsType<Project>(machineARepository.GetProjectById(machineAProjectId));
        Assert.Equal("conflict-from-a", retainedBeforeReview.Tags);
        ProjectMetadataConflictRecord conflict = Assert.Single(machineAConfig.Load().Advanced.ProjectMetadataConflicts);
        Assert.Equal(["tags"], conflict.ConflictingFields);
        Assert.Equal("conflict-from-a", conflict.Local.Tags);
        Assert.Equal("conflict-from-b", conflict.Imported.Tags);

        var restartedMachineAConfig = new FileBackedConfigStore(machineAState.Path);
        AppConfig restartedConfig = restartedMachineAConfig.Load();
        ProjectMetadataConflictRecord restartedConflict = Assert.Single(restartedConfig.Advanced.ProjectMetadataConflicts);
        var resolver = new ProjectMetadataResolutionService(
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero)));
        ProjectMetadataResolutionResult resolution = resolver.Resolve(
            machineARepository,
            restartedConfig,
            restartedConflict.ProjectId,
            restartedConflict.ProjectExternalId,
            ProjectMetadataResolutionDecision.AcceptImported);
        restartedMachineAConfig.Save(restartedConfig);

        Assert.Equal(ProjectMetadataResolutionDecision.AcceptImported, resolution.Decision);
        Assert.Equal("conflict-from-b", machineARepository.GetProjectById(machineAProjectId)?.Tags);
        Assert.Empty(restartedMachineAConfig.Load().Advanced.ProjectMetadataConflicts);
        ProjectMetadataResolutionRecord durableResolution = Assert.Single(
            restartedMachineAConfig.Load().Advanced.ProjectMetadataResolutions);
        Assert.True(durableResolution.UndoAvailable);
        Assert.Equal("accept-imported", durableResolution.Decision);

        MetadataSyncService restartedMachineA = CreateSync(
            machineARepository,
            restartedMachineAConfig,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        AssertSuccess(restartedMachineA.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));
        Assert.Empty(restartedMachineAConfig.Load().Advanced.ProjectMetadataConflicts);

        AppConfig undoConfig = restartedMachineAConfig.Load();
        ProjectMetadataUndoResult undo = resolver.UndoLatest(machineARepository, undoConfig);
        restartedMachineAConfig.Save(undoConfig);
        Assert.Equal(externalId, undo.ProjectExternalId);
        Assert.Equal("conflict-from-a", machineARepository.GetProjectById(machineAProjectId)?.Tags);
        Assert.False(Assert.Single(restartedMachineAConfig.Load().Advanced.ProjectMetadataResolutions).UndoAvailable);

        AssertSuccess(restartedMachineA.ExportProjectToStore(
            sharedRepository.Path,
            machineAProjectId,
            "1.8.7",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        AssertSuccess(machineB.ImportFromStore(sharedRepository.Path, MetadataSyncOptions.Default));
        Assert.Equal("conflict-from-a", machineBRepository.GetProjectById(machineBProjectId)?.Tags);
    }

    private static MetadataSyncService CreateSync(
        SqliteRepository repository,
        IAppConfigStore configStore,
        string installationId) =>
        new(
            repository,
            configStore: configStore,
            installationIdentityProvider: new FixedIdentityProvider(installationId));

    private static SqliteRepository CreateRepository(string path)
    {
        var repository = new SqliteRepository(path);
        repository.EnsureSchema();
        return repository;
    }

    private static int AddProject(SqliteRepository repository, string externalId, string rootPath) =>
        repository.AddProject(new Project
        {
            ExternalId = externalId,
            Name = "Two-machine qualification",
            RootPath = rootPath,
            Preset = "generic",
            CreatedUtc = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc),
            RestoreMode = ProjectRestoreMode.Direct,
            VerificationPolicy = ProjectVerificationPolicy.Always,
            Tags = "baseline"
        });

    private static void AssertSuccess(MetadataSyncResult result) =>
        Assert.Equal(MetadataSyncStatus.Success, result.Status);

    private sealed class FixedIdentityProvider(string installationId) : IInstallationIdentityProvider
    {
        public string GetOrCreate() => installationId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FileBackedConfigStore : IAppConfigStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _configPath;
        private readonly string _databasePath;

        public FileBackedConfigStore(string rootPath)
        {
            _configPath = Path.Combine(rootPath, "appsettings.json");
            _databasePath = Path.Combine(rootPath, "vaultsync.db");
            if (!File.Exists(_configPath))
                Save(new AppConfig { DbPath = _databasePath });
        }

        public bool WasConfigMissingOnFirstLoad => false;

        public AppConfig GetSnapshot() => Load();

        public AppConfig Load() =>
            JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), JsonOptions)
            ?? throw new InvalidDataException("Qualification config is invalid.");

        public void Save(AppConfig config) =>
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, JsonOptions));

        public Task SaveAsync(AppConfig config, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Save(config);
            return Task.CompletedTask;
        }

        public string GetDefaultDbPath() => _databasePath;

        public string ResolveDbPath(AppConfig config = null) =>
            string.IsNullOrWhiteSpace(config?.DbPath) ? _databasePath : config.DbPath;
    }
}
