using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliSnapshotInspectionTests
{
    [Fact]
    public async Task StructuredHistoryIsReadOnlyLimitedAndKeepsLegacyJsonShape()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "History [project]", root.Path);
        int older = repository.CreateSnapshot(projectId, 3, 120);
        int newer = repository.CreateSnapshot(projectId, 5, 240);

        var result = await RunAsync("snapshots", "list", "History [project]", "--db", database,
            "--limit", "1", "--output", "json");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        AssertEnvelope(result.Json, "snapshots.list", 0);
        JsonElement data = result.Json.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(newer, data.GetProperty("snapshots")[0].GetProperty("id").GetInt32());
        Assert.EndsWith("Z", data.GetProperty("snapshots")[0].GetProperty("createdUtc").GetString());
        Assert.Equal(projectId, data.GetProperty("project").GetProperty("id").GetInt32());

        var legacy = await RunAsync("history", "History [project]", "--db", database, "--json");
        Assert.Equal(JsonValueKind.Array, legacy.Json.ValueKind);
        Assert.Equal(2, legacy.Json.GetArrayLength());
        Assert.Equal(new[] { "Id", "CreatedUtc", "FileCount", "TotalBytes" },
            legacy.Json[0].EnumerateObject().Select(property => property.Name));
        Assert.Contains(legacy.Json.EnumerateArray().Select(row => row.GetProperty("Id").GetInt32()), id => id == older);
    }

    [Theory]
    [InlineData("missing_project")]
    [InlineData("invalid_limit")]
    [InlineData("output_conflict")]
    [InlineData("unknown_option")]
    public async Task StructuredHistoryFailuresAreVersionedAndDoNotCreateAStore(string scenario)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        string[] arguments = scenario switch
        {
            "invalid_limit" => ["snapshots", "list", "project", "--limit", "0"],
            "output_conflict" => ["snapshots", "list", "project", "--json"],
            "unknown_option" => ["snapshots", "list", "project", "--unknown"],
            _ => ["snapshots", "list", "project"]
        };
        var result = await RunAsync([.. arguments, "--db", database, "--output", "json"]);
        int expectedExit = scenario == "missing_project" ? 1 : 2;
        AssertEnvelope(result.Json, "snapshots.list", expectedExit);
        Assert.Equal(expectedExit == 1 ? "repository_unavailable" : "invalid_options",
            result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task ExistingStoreReportsMissingProjectWithoutMutation()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        _ = TestRepository.Create(database);
        byte[] before = File.ReadAllBytes(database);
        var result = await RunAsync("history", "absent", "--db", database, "--output", "json");
        AssertEnvelope(result.Json, "snapshots.list", 2);
        Assert.Equal("project_not_found", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(database));
    }

    [Fact]
    public async Task StructuredDiffIsBoundedDeterministicAndKeepsLegacyJsonShape()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Diff project", root.Path);
        DateTime time = DateTime.UtcNow;
        int older = repository.CreateSnapshot(projectId, 3, 30);
        repository.InsertFiles(older,
        [
            new("same.txt", 10, time, "same"),
            new("modified.txt", 10, time, "old"),
            new("removed.txt", 10, time, "removed")
        ]);
        int newer = repository.CreateSnapshot(projectId, 4, 50);
        repository.InsertFiles(newer,
        [
            new("same.txt", 10, time, "same"),
            new("modified.txt", 20, time, "new"),
            new("z-added.txt", 10, time, "z"),
            new("a-added.txt", 10, time, "a")
        ]);

        var result = await RunAsync("snapshots", "diff", "Diff project", newer.ToString(), older.ToString(),
            "--db", database, "--limit", "1", "--output", "json");
        AssertEnvelope(result.Json, "snapshots.diff", 0);
        JsonElement data = result.Json.GetProperty("data");
        Assert.Equal(older, data.GetProperty("fromSnapshotId").GetInt32());
        Assert.Equal(newer, data.GetProperty("toSnapshotId").GetInt32());
        Assert.Equal("a-added.txt", data.GetProperty("paths").GetProperty("added")[0].GetString());
        Assert.Equal(2, data.GetProperty("summary").GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("summary").GetProperty("deleted").GetInt32());
        Assert.Equal(1, data.GetProperty("summary").GetProperty("modified").GetInt32());
        Assert.Equal(1, data.GetProperty("summary").GetProperty("unchanged").GetInt32());
        Assert.True(data.GetProperty("pathsTruncated").GetBoolean());

        var legacy = await RunAsync("diff", "Diff project", newer.ToString(), older.ToString(), "--db", database, "--json");
        Assert.Equal(new[] { "A", "B", "added", "deleted", "modified", "unchanged", "summary" },
            legacy.Json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, legacy.Json.GetProperty("added").GetArrayLength());
    }

    [Fact]
    public async Task DiffRejectsSnapshotIdsFromAnotherProjectOnBothRoutes()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int selectedProject = TestRepository.AddProject(repository, "Selected", root.Path);
        int otherProject = TestRepository.AddProject(repository, "Other", root.Path);
        int selectedSnapshot = repository.CreateSnapshot(selectedProject, 0, 0);
        int foreignSnapshot = repository.CreateSnapshot(otherProject, 0, 0);

        var structured = await RunAsync("snapshots", "diff", "Selected", selectedSnapshot.ToString(),
            foreignSnapshot.ToString(), "--db", database, "--output", "json");
        AssertEnvelope(structured.Json, "snapshots.diff", 2);
        Assert.Equal("snapshot_not_found", structured.Json.GetProperty("error").GetProperty("code").GetString());
        int legacyExit = await Program.Main(
            ["diff", "Selected", selectedSnapshot.ToString(), foreignSnapshot.ToString(), "--db", database, "--json"]);
        Assert.NotEqual(0, legacyExit);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    public async Task StructuredDiffRejectsInvalidArgumentsBeforeDatabaseAccess(string scenario)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        string[] arguments = scenario switch
        {
            "limit" => ["snapshots", "diff", "project", "--limit", "0"],
            "conflict" => ["snapshots", "diff", "project", "--json"],
            _ => ["snapshots", "diff", "project", "--unknown"]
        };
        var result = await RunAsync([.. arguments, "--db", database, "--output", "json"]);
        AssertEnvelope(result.Json, "snapshots.diff", 2);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
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
