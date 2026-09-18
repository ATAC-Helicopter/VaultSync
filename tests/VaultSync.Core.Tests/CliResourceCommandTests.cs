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

public sealed class CliResourceCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectRoutesPreserveRegistrationPathUpdatesAndUserFiles(bool groupedRoute)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string moved = Directory.CreateDirectory(Path.Combine(root.Path, "moved")).FullName;
        string sentinel = Path.Combine(source, "user.txt");
        File.WriteAllText(sentinel, "keep this source data");
        string name = "Project [with markup] and spaces";

        Assert.Equal(0, await Program.Main(ProjectArgs(groupedRoute, "add", "add-project",
            name, source, "--db", database, "--quiet")));
        var repository = new SqliteRepository(database);
        Assert.Equal(source, repository.GetProjectByName(name)!.RootPath);

        JsonElement[] projects = await ReadJsonAsync(ProjectArgs(groupedRoute, "list", "list-projects",
            "--db", database, "--json"));
        Assert.Equal(name, Assert.Single(projects).GetProperty("Name").GetString());

        Assert.Equal(0, await Program.Main(ProjectArgs(groupedRoute, "set-path", "set-path",
            name, moved, "--db", database, "--quiet")));
        Assert.Equal(moved, repository.GetProjectByName(name)!.RootPath);

        Assert.Equal(0, await Program.Main(ProjectArgs(groupedRoute, "remove", "remove-project",
            name, "--db", database, "--yes", "--quiet")));
        Assert.Null(repository.GetProjectByName(name));
        Assert.Equal("keep this source data", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(moved));
    }

    [Fact]
    public async Task GroupedSnapshotsCaptureChangesWithoutCreatingStoredBackupsAndPruneOnlyThePlan()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string file = Path.Combine(source, "state.txt");
        File.WriteAllText(file, "first");
        Assert.Equal(0, await Program.Main(["projects", "add", "Snapshot project", source,
            "--db", database, "--quiet"]));
        Assert.Equal(0, await Program.Main(["snapshots", "create", "Snapshot project",
            "--db", database, "--quiet"]));
        File.WriteAllText(file, "second and changed");
        Assert.Equal(0, await Program.Main(["snapshots", "create", "Snapshot project",
            "--db", database, "--quiet"]));

        var repository = new SqliteRepository(database);
        var snapshots = repository.GetSnapshotsForProject("Snapshot project").ToList();
        Assert.Equal(2, snapshots.Count);
        Assert.Empty(repository.GetBackupsForProject(repository.GetProjectByName("Snapshot project")!.Id));
        Assert.NotEqual(repository.GetFilesForSnapshot(snapshots[0].Id).Single().HashSha256,
            repository.GetFilesForSnapshot(snapshots[1].Id).Single().HashSha256);

        JsonElement[] history = await ReadJsonAsync(["snapshots", "list", "Snapshot project",
            "--db", database, "--limit", "1", "--json"]);
        Assert.Equal(snapshots[0].Id, Assert.Single(history).GetProperty("Id").GetInt32());
        Assert.Equal(0, await Program.Main(["snapshots", "prune", "Snapshot project", "--db", database,
            "--keep-last", "1", "--dry-run", "--quiet"]));
        Assert.Equal(2, repository.GetSnapshotsForProject("Snapshot project").Count());
        Assert.Equal("second and changed", File.ReadAllText(file));
        Assert.Equal(0, await Program.Main(["snapshots", "prune", "Snapshot project", "--db", database,
            "--keep-last", "1", "--quiet"]));
        Assert.Single(repository.GetSnapshotsForProject("Snapshot project"));
        Assert.Equal("second and changed", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietRemovalRequiresExplicitConfirmationAndPreservesLocalHistory(bool groupedRoute)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string sentinel = Path.Combine(source, "state.txt");
        File.WriteAllText(sentinel, "user data");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Protected registration", source);
        repository.CreateSnapshot(projectId, 1, 9);

        int result = await Program.Main(ProjectArgs(groupedRoute, "remove", "remove-project",
            "Protected registration", "--db", database, "--quiet"));

        Assert.NotEqual(0, result);
        Assert.NotNull(repository.GetProjectByName("Protected registration"));
        Assert.Single(repository.GetSnapshotsForProject("Protected registration"));
        Assert.Equal("user data", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task DiscoveryWithExplicitRootListsCandidateFoldersWithoutRegisteringThem()
    {
        using var root = new TempDirectory();
        string candidate = Directory.CreateDirectory(Path.Combine(root.Path, "Candidate [source] folder")).FullName;
        File.WriteAllText(Path.Combine(root.Path, "unrelated.txt"), "not a project");

        JsonElement[] discovered = await ReadJsonAsync(["projects", "discover", "--root", root.Path, "--json"]);

        JsonElement project = Assert.Single(discovered);
        Assert.Equal("Candidate [source] folder", project.GetProperty("Name").GetString());
        Assert.Equal(candidate, project.GetProperty("Path").GetString());
        Assert.Equal(JsonValueKind.Null, project.GetProperty("LastSnapshotTime").ValueKind);
        Assert.Equal(JsonValueKind.Null, project.GetProperty("LastSnapshotSizeBytes").ValueKind);
        Assert.Single(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories));
        Assert.Equal("not a project", File.ReadAllText(Path.Combine(root.Path, "unrelated.txt")));
    }

    private static string[] ProjectArgs(bool grouped, string action, string legacy, params string[] arguments)
        => grouped ? ["projects", action, .. arguments] : [legacy, .. arguments];

    private static async Task<JsonElement[]> ReadJsonAsync(string[] args)
    {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await Program.Main(args));
        }
        finally
        {
            Console.SetOut(original);
        }
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        return json.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }
}
