using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliProjectInspectionTests
{
    [Fact]
    public async Task StructuredListFiltersBeforeLimitingAndPreservesLegacyJsonShape()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        repository.AddProject(new Project { Name = "Alpha [one]", RootPath = root.Path, Preset = "custom" });
        int selectedId = repository.AddProject(new Project { Name = "Alpha two", RootPath = root.Path, Preset = "dotnet" });
        repository.AddProject(new Project { Name = "Alpha three", RootPath = root.Path, Preset = "dotnet" });
        repository.AddProject(new Project { Name = "Beta", RootPath = root.Path, Preset = "dotnet" });

        var result = await RunAsync("projects", "list", "--db", database,
            "--filter", "ALPHA", "--preset", "DOTNET", "--limit", "1", "--output", "json");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        AssertEnvelope(result.Json, "projects.list", 0);
        JsonElement data = result.Json.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(2, data.GetProperty("matchedCount").GetInt32());
        Assert.Equal(4, data.GetProperty("totalCount").GetInt32());
        Assert.Equal("Alpha three", data.GetProperty("projects")[0].GetProperty("name").GetString());
        Assert.True(data.GetProperty("projects")[0].GetProperty("id").GetInt32() > 0);
        Assert.EndsWith("Z", data.GetProperty("projects")[0].GetProperty("createdUtc").GetString());

        var show = await RunAsync("projects", "show", "--id", selectedId.ToString(), "--db", database, "--output", "json");
        Assert.Equal("Alpha two", show.Json.GetProperty("data").GetProperty("project").GetProperty("name").GetString());
        var legacy = await RunAsync("list-projects", "--db", database, "--json");
        Assert.Equal(JsonValueKind.Array, legacy.Json.ValueKind);
        Assert.Equal(4, legacy.Json.GetArrayLength());
        Assert.Equal(new[] { "Name", "RootPath", "Preset", "CreatedUtc" },
            legacy.Json[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task NumericNamesAreLiteralAndExplicitIdsRemainUnambiguous()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        int first = TestRepository.AddProject(repository, "First project", root.Path);
        int numeric = TestRepository.AddProject(repository, first.ToString(), root.Path);

        var byName = await RunAsync("projects", "show", first.ToString(), "--db", database, "--output", "json");
        Assert.Equal(numeric, byName.Json.GetProperty("data").GetProperty("project").GetProperty("id").GetInt32());
        var byId = await RunAsync("projects", "show", "--id", first.ToString(), "--db", database, "--output", "json");
        Assert.Equal("First project", byId.Json.GetProperty("data").GetProperty("project").GetProperty("name").GetString());
        var absent = await RunAsync("projects", "show", "does not exist", "--db", database, "--output", "json");
        AssertEnvelope(absent.Json, "projects.show", 2);
        Assert.Equal("project_not_found", absent.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(absent.Error);
    }

    [Theory]
    [InlineData("selector_conflict")]
    [InlineData("missing_selector")]
    [InlineData("invalid_id")]
    [InlineData("invalid_limit")]
    [InlineData("output_conflict")]
    [InlineData("blank_filter")]
    public async Task InvalidStructuredRequestsFailBeforeCreatingAStore(string scenario)
    {
        using var root = new TempDirectory();
        string missing = Path.Combine(root.Path, "never-created", "vault.db");
        string[] arguments = scenario switch
        {
            "selector_conflict" => ["projects", "show", "name", "--id", "1"],
            "missing_selector" => ["projects", "show"],
            "invalid_id" => ["projects", "show", "--id", "0"],
            "invalid_limit" => ["projects", "list", "--limit", "0"],
            "output_conflict" => ["projects", "list", "--json"],
            _ => ["projects", "list", "--filter", " "]
        };
        var result = await RunAsync([.. arguments, "--db", missing, "--output", "json"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, result.Json.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("data").ValueKind);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task StructuredInspectionDoesNotInitializeMissingStoresOrOverwriteInvalidFiles(string action)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        string[] args = action == "show" ? ["projects", action, "--id", "1"] : ["projects", action];
        var missing = await RunAsync([.. args, "--db", database, "--output", "json"]);
        AssertEnvelope(missing.Json, "projects." + action, 1);
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
        database = Path.Combine(root.Path, "user-data.db");
        File.WriteAllText(database, "unrelated user data");
        var invalid = await RunAsync([.. args, "--db", database, "--output", "json"]);
        Assert.Equal("repository_unavailable", invalid.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("unrelated user data", File.ReadAllText(database));
    }

    [Theory]
    [InlineData("invalid_id")]
    [InlineData("invalid_limit")]
    [InlineData("unknown_option")]
    [InlineData("missing_value")]
    public async Task ArgumentParsingFailuresRemainVersionedJson(string scenario)
    {
        string[] args = scenario switch
        {
            "invalid_id" => ["projects", "show", "--id", "not-an-integer"],
            "invalid_limit" => ["projects", "list", "--limit", "not-an-integer"],
            "unknown_option" => ["projects", "list", "--unknown-option"],
            _ => ["projects", "show", "--id"]
        };
        var result = await RunAsync([.. args, "--output", "json"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, result.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(result.Error);
    }

    [Fact]
    public void ReadOnlyRepositoryQueriesExistingDataButRejectsWrites()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        var writable = TestRepository.Create(database);
        int id = TestRepository.AddProject(writable, "Keep", root.Path);
        var readOnly = new SqliteRepository(database, readOnly: true);
        Assert.Equal("Keep", readOnly.GetProjectById(id)!.Name);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => readOnly.RemoveProject(id));
        Assert.NotNull(writable.GetProjectById(id));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task HumanInspectionRendersLiteralNamesAndLocalIds(string action)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        var repository = TestRepository.Create(database);
        int id = TestRepository.AddProject(repository, "Human [project] name", root.Path);
        Spectre.Console.IAnsiConsole original = Spectre.Console.AnsiConsole.Console;
        using var output = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(output),
            Ansi = Spectre.Console.AnsiSupport.No
        });
        console.Profile.Width = 200;
        try
        {
            Spectre.Console.AnsiConsole.Console = console;
            string[] args = action == "show" ? ["projects", action, "--id", id.ToString()] : ["projects", action];
            Assert.Equal(0, await Program.Main([.. args, "--db", database, "--output", "text"]));
        }
        finally
        {
            Spectre.Console.AnsiConsole.Console = original;
        }
        Assert.Contains("Human [project] name", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ID", output.ToString(), StringComparison.Ordinal);
        Assert.Single(repository.ListProjects());
    }

    private static void AssertEnvelope(JsonElement json, string operation, int exitCode)
    {
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(operation, json.GetProperty("operation").GetString());
        Assert.Equal(exitCode, json.GetProperty("exitCode").GetInt32());
        Assert.Equal(exitCode == 0 ? "success" : "error", json.GetProperty("status").GetString());
    }

    private static async Task<(int ExitCode, JsonElement Json, string Error)> RunAsync(params string[] args)
    {
        _ = Spectre.Console.AnsiConsole.Console;
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
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
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return (code, document.RootElement.Clone(), error.ToString());
    }
}
