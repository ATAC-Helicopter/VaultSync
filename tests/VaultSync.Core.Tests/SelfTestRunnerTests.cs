using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.CLI.Commands;
using VaultSync.CLI.Services;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SelfTestRunnerTests
{
    [Fact]
    public async Task RunAsync_DefaultDatabase_IsIsolatedAndRemoved()
    {
        using var temp = new TempDirectory();
        string runRoot = Path.Combine(temp.Path, "runs");
        var runner = CreateRunner(runRoot);

        SelfTestRunResult result = await runner.RunAsync(
            explicitDatabasePath: null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.UsesTemporaryDatabase);
        Assert.Equal(0, result.SyncExitCode);
        Assert.Equal(0, result.VerificationFailures);
        Assert.False(Directory.Exists(result.WorkspacePath));
        Assert.StartsWith(
            Path.GetFullPath(runRoot),
            Path.GetFullPath(result.DatabasePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExplicitDatabase_RemovesOnlyTemporaryMetadata()
    {
        using var temp = new TempDirectory();
        string databasePath = Path.Combine(temp.Path, "user", "vaultsync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var runner = CreateRunner(Path.Combine(temp.Path, "runs"));

        SelfTestRunResult result = await runner.RunAsync(
            databasePath,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.UsesTemporaryDatabase);
        Assert.True(File.Exists(databasePath));
        Assert.True(Directory.Exists(result.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(result.WorkspacePath, "dst", "a.txt")));

        var repository = new SqliteRepository(databasePath);
        repository.EnsureSchema();
        Assert.Null(repository.GetProjectByName(result.ProjectName));
    }

    [Fact]
    public async Task RunAsync_SyncFailure_ReturnsFailureAndCleansIsolatedState()
    {
        using var temp = new TempDirectory();
        var runner = new SelfTestRunner(
            () => new TestSyncRunner(exitCode: 9),
            Path.Combine(temp.Path, "runs"));

        SelfTestRunResult result = await runner.RunAsync(
            explicitDatabasePath: null,
            CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(9, result.SyncExitCode);
        Assert.Null(result.VerificationFailures);
        Assert.False(Directory.Exists(result.WorkspacePath));
    }

    [Fact]
    public async Task RunAsync_CancelledRun_CleansIsolatedState()
    {
        using var temp = new TempDirectory();
        string runRoot = Path.Combine(temp.Path, "runs");
        var runner = CreateRunner(runRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(explicitDatabasePath: null, cancellation.Token));

        Assert.Empty(Directory.EnumerateDirectories(runRoot));
    }

    [Fact]
    public void WriteResult_HandlesSuccessAndBothFailureStages()
    {
        SelfTestCommand.WriteResult(CreateResult(
            usesTemporaryDatabase: true,
            syncExitCode: 0,
            verificationFailures: 0));
        SelfTestCommand.WriteResult(CreateResult(
            usesTemporaryDatabase: true,
            syncExitCode: 9,
            verificationFailures: null));
        SelfTestCommand.WriteResult(CreateResult(
            usesTemporaryDatabase: false,
            syncExitCode: 0,
            verificationFailures: 2));
        SelfTestCommand.WriteResult(CreateResult(
            usesTemporaryDatabase: false,
            syncExitCode: 0,
            verificationFailures: 0));
    }

    private static SelfTestRunner CreateRunner(string runRoot) =>
        new(() => new TestSyncRunner(exitCode: 0), runRoot);

    private static SelfTestRunResult CreateResult(
        bool usesTemporaryDatabase,
        int syncExitCode,
        int? verificationFailures) =>
        new(
            ExitCode: syncExitCode == 0 && verificationFailures == 0 ? 0 : 2,
            UsesTemporaryDatabase: usesTemporaryDatabase,
            DatabasePath: "/tmp/selftest.db",
            WorkspacePath: "/tmp/selftest",
            ProjectName: "SelfTest-test",
            ProjectId: 1,
            SnapshotId: 1,
            SyncExitCode: syncExitCode,
            VerificationFailures: verificationFailures);

    private sealed class TestSyncRunner(int exitCode) : ISyncRunner
    {
        public string Name => "test";

        public Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            if (exitCode != 0)
                return Task.FromResult(exitCode);

            foreach (string sourcePath in Directory.EnumerateFiles(
                         project.RootPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(project.RootPath, sourcePath);
                string destinationPath = Path.Combine(destination, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            return Task.FromResult(0);
        }
    }
}
