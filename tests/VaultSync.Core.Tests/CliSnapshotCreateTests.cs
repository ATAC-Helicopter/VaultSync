using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliSnapshotCreateTests
{
    [Fact]
    public async Task VersionedCreateReturnsOneResultAndPersistsSnapshot()
    {
        using var root = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "state.txt"), "snapshot content");
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "Photos", source);

        var result = await RunAsync("snapshots", "create", "Photos", "--db", database, "--output", "json");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal(1, result.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("snapshots.create", result.Json.GetProperty("operation").GetString());
        Assert.Equal(1, result.Json.GetProperty("data").GetProperty("fileCount").GetInt32());
        int id = result.Json.GetProperty("data").GetProperty("snapshotId").GetInt32();
        Assert.NotNull(repository.GetSnapshotById(id));
    }

    [Fact]
    public async Task VersionedCreateRejectsUnknownProjectWithoutChangingStore()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        var result = await RunAsync("snapshots", "create", "Missing", "--db", database, "--output", "json");
        Assert.Equal(2, result.Code);
        Assert.Equal("project_not_found", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(repository.GetAllProjects());
    }

    private static async Task<(int Code, JsonElement Json, string Error)> RunAsync(params string[] args)
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
