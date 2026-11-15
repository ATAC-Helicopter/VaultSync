using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class StableCliRemovalBackportTests
{
    [Fact]
    public async Task QuietRemoveWithoutExplicitYesLeavesRegistrationAndHistoryIntact()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string userFile = Path.Combine(source, "data.txt");
        File.WriteAllText(userFile, "keep");
        SqliteRepository repo = TestRepository.Create(db);
        int projectId = TestRepository.AddProject(repo, "Protected", source);
        repo.CreateSnapshot(projectId, 1, 4);

        Assert.Equal(2, await Program.Main(["remove-project", "Protected", "--db", db, "--quiet"]));

        Assert.NotNull(repo.GetProjectByName("Protected"));
        Assert.Single(repo.GetSnapshotsForProject("Protected"));
        Assert.Equal("keep", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task ExplicitYesQuietRemoveChangesOnlyRegistrationAndLocalIndex()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string userFile = Path.Combine(source, "data.txt");
        File.WriteAllText(userFile, "keep");
        SqliteRepository repo = TestRepository.Create(db);
        int projectId = TestRepository.AddProject(repo, "Protected", source);
        repo.CreateSnapshot(projectId, 1, 4);

        Assert.Equal(0, await Program.Main(["remove-project", "Protected", "--db", db, "--quiet", "--yes"]));

        Assert.Null(repo.GetProjectByName("Protected"));
        Assert.Equal("keep", File.ReadAllText(userFile));
    }
}
