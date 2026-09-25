#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI.Commands;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliWatchCommandTests
{
    [Fact]
    public async Task CancellationBeforeStartupDoesNotInitializeDatabase()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(130, await WatchCommand.RunAsync(new WatchSettings
        {
            ProjectName = "unused", DbPath = database, Quiet = true
        }, cancelled.Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task FailedStartupMirrorStopsSessionWithNonzeroExit()
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "content.txt"), "content");
        string destination = Path.Combine(root.Path, "destination-file");
        File.WriteAllText(destination, "not a directory");
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "watched", source);

        IAnsiConsole previous = AnsiConsole.Console;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        TextWriter previousError = Console.Error;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });
        Console.SetError(errors);
        try
        {
            int exit = await WatchCommand.RunAsync(new WatchSettings
            {
                ProjectName = "watched", DbPath = database, Destination = destination,
                Sync = true, Quiet = true
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(2, exit);
            Assert.Equal("", output.ToString());
            Assert.Contains("failed", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(previousError);
            AnsiConsole.Console = previous;
        }
    }

    [Fact]
    public async Task FailedLaterMirrorStopsWatchingAndDrainsPendingWork()
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string sourceFile = Path.Combine(source, "content.txt");
        File.WriteAllText(sourceFile, "startup");
        string destination = Path.Combine(root.Path, "destination");
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "watched", source);
        using var stopping = new CancellationTokenSource();
        using var errors = new StringWriter();
        TextWriter previousError = Console.Error;
        Console.SetError(errors);
        Task<int>? session = null;
        try
        {
            session = WatchCommand.RunAsync(new WatchSettings
            {
                ProjectName = "watched", DbPath = database, Destination = destination,
                Sync = true, Quiet = true, DebounceMs = 100
            }, stopping.Token);
            string mirroredFile = Path.Combine(destination, "content.txt");
            using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(mirroredFile) && !session.IsCompleted)
                await Task.Delay(20, startupDeadline.Token);
            Assert.False(session.IsCompleted);

            Directory.Delete(destination, recursive: true);
            File.WriteAllText(destination, "not a directory");
            File.WriteAllText(sourceFile, "trigger failed mirror");
            Assert.Equal(2, await session.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("failed", errors.ToString(), StringComparison.OrdinalIgnoreCase);
            int snapshotCount = repository.GetSnapshotsForProject("watched").Count();
            File.WriteAllText(sourceFile, "after shutdown");
            await Task.Delay(300);
            Assert.Equal(snapshotCount, repository.GetSnapshotsForProject("watched").Count());
        }
        finally
        {
            stopping.Cancel();
            if (session is not null)
                await session.WaitAsync(TimeSpan.FromSeconds(5));
            Console.SetError(previousError);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietWatchUsesExplicitDatabaseAndStopsWithoutLaterSnapshots(bool pendingChange)
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string file = Path.Combine(source, "content.txt");
        File.WriteAllText(file, "startup");
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "watched", source);
        using var stopping = new CancellationTokenSource();
        IAnsiConsole previous = AnsiConsole.Console;
        using var output = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output) });
        Task<int>? session = null;
        try
        {
            session = WatchCommand.RunAsync(new WatchSettings
            {
                ProjectName = "watched", DbPath = database, Quiet = true, DebounceMs = 1000
            }, stopping.Token);
            // Snapshot creation is asynchronous; wait for its committed result.
            using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!repository.GetSnapshotsForProject("watched").Any() && !session.IsCompleted)
                await Task.Delay(10, startupDeadline.Token);
            Assert.Single(repository.GetSnapshotsForProject("watched"));
            await Task.Delay(100);
            Assert.False(session.IsCompleted);
            if (pendingChange)
            {
                File.WriteAllText(file, "pending change");
                await Task.Delay(100);
            }
            stopping.Cancel();
            Assert.Equal(130, await session.WaitAsync(TimeSpan.FromSeconds(5)));
            File.WriteAllText(file, "after shutdown");
            await Task.Delay(1200);
            Assert.Single(repository.GetSnapshotsForProject("watched"));
            Assert.Equal("after shutdown", File.ReadAllText(file));
            Assert.Equal("", output.ToString());
        }
        finally
        {
            stopping.Cancel();
            if (session is not null)
                await session.WaitAsync(TimeSpan.FromSeconds(5));
            AnsiConsole.Console = previous;
        }
    }
}
