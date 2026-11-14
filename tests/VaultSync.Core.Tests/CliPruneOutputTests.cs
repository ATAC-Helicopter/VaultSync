using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliPruneOutputTests
{
    [Fact]
    public async Task VersionedPrunePreviewsAndDeletesOnlySelectedBatch()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Prune", source);
        for (int index = 0; index < 4; index++)
            repository.CreateSnapshot(projectId, DateTime.UtcNow.AddDays(-index).Ticks, 0);

        var preview = await RunJsonAsync("snapshots", "prune", "Prune", "--keep-last", "0", "--limit", "2",
            "--dry-run", "--db", database, "--output", "json");
        Assert.Equal(0, preview.Code);
        Assert.Equal(4, preview.Json.GetProperty("data").GetProperty("plannedCount").GetInt32());
        Assert.Equal(2, preview.Json.GetProperty("data").GetProperty("remainingCount").GetInt32());
        Assert.Equal(4, repository.GetSnapshotsForProject("Prune").Count());

        var applied = await RunJsonAsync("prune", "Prune", "--keep-last", "0", "--limit", "2",
            "--db", database, "--output", "json");
        Assert.Equal(0, applied.Code);
        Assert.Equal("snapshots.prune", applied.Json.GetProperty("operation").GetString());
        Assert.Equal(2, applied.Json.GetProperty("data").GetProperty("deletedSnapshots").GetInt32());
        Assert.Equal(2, repository.GetSnapshotsForProject("Prune").Count());

        var legacy = await RunJsonAsync("prune", "Prune", "--keep-last", "0", "--db", database, "--json");
        Assert.Equal(0, legacy.Code);
        Assert.Equal(2, legacy.Json.GetProperty("plannedDeletions").GetArrayLength());
        Assert.Empty(repository.GetSnapshotsForProject("Prune"));
    }

    [Fact]
    public async Task VersionedPruneRejectsMissingStoreWithoutCreatingIt()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "absent", "vault.db");
        var result = await RunJsonAsync("snapshots", "prune", "Missing", "--keep-last", "0",
            "--dry-run", "--db", database, "--output", "json");
        Assert.Equal(1, result.Code);
        Assert.Equal("repository_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task VersionedPruneRejectsInvalidBatchSizeBeforeDatabaseAccess()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "absent", "vault.db");
        var result = await RunJsonAsync("snapshots", "prune", "Missing", "--keep-last", "0",
            "--limit", "0", "--db", database, "--output", "json");
        Assert.Equal(2, result.Code);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    private static async Task<(int Code, JsonElement Json)> RunJsonAsync(params string[] arguments)
    {
        _ = AnsiConsole.Console;
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            int code = await Program.Main(arguments);
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            return (code, json.RootElement.Clone());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}
