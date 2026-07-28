using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;

namespace VaultSync.CLI.Services;

internal sealed class SelfTestRunner(
    Func<ISyncRunner>? syncRunnerFactory = null,
    string? temporaryBasePath = null)
{
    private readonly Func<ISyncRunner> _syncRunnerFactory =
        syncRunnerFactory ?? CreateSyncRunner;
    private readonly string _temporaryBasePath = temporaryBasePath
        ?? Path.Combine(Path.GetTempPath(), "vaultsync-selftest");

    public async Task<SelfTestRunResult> RunAsync(
        string? explicitDatabasePath,
        CancellationToken cancellationToken)
    {
        string runId = Guid.NewGuid().ToString("N");
        string workspacePath = Path.Combine(_temporaryBasePath, runId);
        string sourcePath = Path.Combine(workspacePath, "src");
        string destinationPath = Path.Combine(workspacePath, "dst");
        bool usesTemporaryDatabase = string.IsNullOrWhiteSpace(explicitDatabasePath);
        string databasePath = usesTemporaryDatabase
            ? Path.Combine(workspacePath, "selftest.db")
            : explicitDatabasePath!;
        string projectName = $"SelfTest-{DateTime.UtcNow:yyyyMMddHHmmss}-{runId[..6]}";
        SqliteRepository? repository = null;
        bool projectRegistered = false;

        try
        {
            await CreateFixtureAsync(sourcePath, destinationPath, cancellationToken);

            repository = new SqliteRepository(databasePath);
            repository.EnsureSchema();
            int projectId = repository.AddProject(
                new Project { Name = projectName, RootPath = sourcePath, Preset = "custom" });
            projectRegistered = true;
            Project project = repository.GetProjectByName(projectName)!;

            var snapshotService = new SnapshotService(repository, new HashService());
            int snapshotId = await snapshotService.CreateSnapshotAsync(
                project,
                fullHash: true,
                maxSnapshotsToKeep: null,
                ct: cancellationToken);

            var syncService = new SyncService(_syncRunnerFactory());
            int syncExitCode = await syncService.SyncAsync(
                project,
                destinationPath,
                dryRun: false,
                cancellationToken);
            if (syncExitCode != 0)
            {
                return new SelfTestRunResult(
                    2,
                    usesTemporaryDatabase,
                    databasePath,
                    workspacePath,
                    projectName,
                    projectId,
                    snapshotId,
                    syncExitCode,
                    VerificationFailures: null);
            }

            var verifyService = new VerifyService(repository, new HashService());
            VerifyResult verification = await verifyService.VerifyAsync(
                project,
                destinationPath,
                percent: 100,
                full: true,
                cancellationToken);

            return new SelfTestRunResult(
                verification.Failures.Count == 0 ? 0 : 2,
                usesTemporaryDatabase,
                databasePath,
                workspacePath,
                projectName,
                projectId,
                snapshotId,
                syncExitCode,
                verification.Failures.Count);
        }
        finally
        {
            if (projectRegistered && !usesTemporaryDatabase)
                TryDeleteProject(repository!, projectName);

            if (usesTemporaryDatabase)
                TryDeleteDirectory(workspacePath);
        }
    }

    private static async Task CreateFixtureAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(destinationPath);
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, "a.txt"),
            "hello",
            cancellationToken);
        string nestedPath = Path.Combine(sourcePath, "Sub");
        Directory.CreateDirectory(nestedPath);
        await File.WriteAllTextAsync(
            Path.Combine(nestedPath, "b.txt"),
            "world",
            cancellationToken);
    }

    private static ISyncRunner CreateSyncRunner() => new SyncServiceRunner();

    private static void TryDeleteProject(SqliteRepository repository, string projectName)
    {
        try
        {
            repository.DeleteProjectCascade(projectName);
        }
        catch
        {
            // Preserve the original self-test result; an explicit database
            // remains inspectable if cleanup itself fails.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed cleanup remains safely isolated under the system temp root.
        }
    }

    private sealed class SyncServiceRunner : ISyncRunner
    {
        private readonly SyncService _service = new();

        public string Name => _service.RunnerName;

        public Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            CancellationToken ct) =>
            _service.SyncAsync(project, destination, dryRun, ct);
    }
}

internal sealed record SelfTestRunResult(
    int ExitCode,
    bool UsesTemporaryDatabase,
    string DatabasePath,
    string WorkspacePath,
    string ProjectName,
    int ProjectId,
    int SnapshotId,
    int SyncExitCode,
    int? VerificationFailures);
