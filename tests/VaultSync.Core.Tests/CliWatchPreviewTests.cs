using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.CLI.Commands;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

[Collection("CLI presentation console")]
public sealed class CliWatchPreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DryRunTerminatesWithoutSnapshotsOrDestinationWrites(bool verify)
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string file = Path.Combine(source, "content.txt");
        File.WriteAllText(file, "unchanged");
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "watched", source);
        byte[] before = File.ReadAllBytes(database);
        string destination = Path.Combine(root.Path, "absent-target");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        int exit = await WatchCommand.RunAsync(new WatchSettings
        {
            ProjectName = "watched", DbPath = database, Destination = destination,
            Sync = verify, Verify = verify, DryRun = true, Quiet = true
        }, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exit);
        Assert.Empty(repository.GetSnapshotsForProject("watched"));
        Assert.Equal(before, File.ReadAllBytes(database));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("unchanged", File.ReadAllText(file));
        File.WriteAllText(file, "after preview");
        await Task.Delay(200);
        Assert.Empty(repository.GetSnapshotsForProject("watched"));
    }

    [Fact]
    public async Task DryRunMissingStoreDoesNotCreateDatabaseOrParent()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "absent", "vault.db");
        int exit = await WatchCommand.RunAsync(new WatchSettings
        {
            ProjectName = "watched", DbPath = database, DryRun = true, Quiet = true
        }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task VersionedPlanReportsIntentWithoutCheckingMissingDestination()
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "private-source")).FullName;
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "watched", source);
        string destination = Path.Combine(root.Path, "private-target");

        var result = await RunJsonAsync("watch", "watched", "--db", database,
            "--dry-run", "--verify", "--dest", destination, "--debounce-ms", "25", "--quiet", "--output", "json");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal("watch.plan", result.Json.GetProperty("operation").GetString());
        Assert.Equal(1, result.Json.GetProperty("schemaVersion").GetInt32());
        var data = result.Json.GetProperty("data");
        Assert.Equal(projectId, data.GetProperty("projectId").GetInt32());
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.True(data.GetProperty("mirror").GetBoolean());
        Assert.True(data.GetProperty("verify").GetBoolean());
        Assert.Equal(100, data.GetProperty("debounceMs").GetInt32());
        foreach (string field in new[] { "watching", "payloadChecked", "destinationChecked", "transferToolChecked", "recordedBackup" })
            Assert.False(data.GetProperty(field).GetBoolean());
        Assert.DoesNotContain(root.Path, result.Json.GetRawText());
        Assert.Empty(repository.GetSnapshotsForProject("watched"));
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("--unknown-option")]
    [InlineData("--debounce-ms", "0")]
    [InlineData("--debounce-ms", "no-number")]
    [InlineData("--verify")]
    public async Task InvalidPlanOptionsFailBeforeDatabaseAccess(params string[] extra)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "absent", "vault.db");
        string[] arguments = ["watch", "watched", "--db", database, "--dry-run", "--output", "json", .. extra];
        var result = await RunJsonAsync(arguments);
        Assert.Equal(2, result.Code);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task LiveJsonFailsBeforeDatabaseAccess()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "absent", "vault.db");
        var result = await RunJsonAsync("watch", "watched", "--db", database, "--output=json");
        Assert.Equal(2, result.Code);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingProjectOrSourceReturnsOneVersionedFailure(bool sourceMissing)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        if (sourceMissing)
            TestRepository.AddProject(repository, "watched", Path.Combine(root.Path, "missing-source"));
        var result = await RunJsonAsync("watch", "watched", "--db", database, "--dry-run", "--output", "json");
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal(sourceMissing ? "source_unavailable" : "project_not_found",
            result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(root.Path, result.Json.GetRawText());
    }

    [Fact]
    public async Task UnsupportedStoreIsNotInitializedOrMigrated()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "old.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(database);
        var result = await RunJsonAsync("watch", "watched", "--db", database, "--dry-run", "--output", "json");
        Assert.Equal(1, result.Code);
        Assert.Equal("planning_failed", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(database));
    }

    [Fact]
    public async Task DefaultPlanDoesNotPersistConfiguration()
    {
        using var config = new TestAppConfigScope();
        var result = await RunJsonAsync("watch", "watched", "--dry-run", "--output", "json");
        Assert.Equal(1, result.Code);
        Assert.Empty(Directory.GetFiles(config.ConfigDirectory));
    }

    [Fact]
    public async Task ConfiguredPlanUsesSharedDatabaseWithoutRewritingConfig()
    {
        using var root = new TempDirectory();
        using var scope = new TestAppConfigScope();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "watched", source);
        VaultSync.Core.Config.AppConfigStore.Save(new VaultSync.Core.Config.AppConfig { DbPath = database });
        string configPath = Path.Combine(scope.ConfigDirectory, "appsettings.json");
        byte[] before = File.ReadAllBytes(configPath);
        var result = await RunJsonAsync("watch", "watched", "--dry-run", "--output", "json");
        Assert.Equal(0, result.Code);
        Assert.Equal(before, File.ReadAllBytes(configPath));
        Assert.Empty(repository.GetSnapshotsForProject("watched"));
    }

    private static async Task<(int Code, JsonElement Json, string Error)> RunJsonAsync(params string[] args)
    {
        _ = Spectre.Console.AnsiConsole.Console;
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            code = await Program.Main(args);
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        return (code, json.RootElement.Clone(), error.ToString());
    }
}
