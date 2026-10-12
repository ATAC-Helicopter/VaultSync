using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliFoundationSafetyTests
{
    [Fact]
    public async Task QuietRemovalPreservesUserDataRegistrationAndHistory()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string userFile = Path.Combine(source, "data.txt");
        File.WriteAllText(userFile, "keep");
        SqliteRepository repo = TestRepository.Create(db);
        int id = TestRepository.AddProject(repo, "Protected", source);
        repo.CreateSnapshot(id, 1, 4);
        Assert.Equal(2, await Program.Main(["remove-project", "Protected", "--db", db, "--quiet"]));
        Assert.NotNull(repo.GetProjectByName("Protected"));
        Assert.Single(repo.GetSnapshotsForProject("Protected"));
        Assert.Equal("keep", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task WatchPreviewReturnsWithoutChangingHistoryOrCreatingTarget()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "data.txt"), "keep");
        SqliteRepository repo = TestRepository.Create(db);
        int id = TestRepository.AddProject(repo, "Protected", source);
        repo.CreateSnapshot(id, 1, 4);
        byte[] original = File.ReadAllBytes(db);
        string target = Path.Combine(root.Path, "target");
        Task<int> run = Program.Main(["watch", "Protected", "--db", db, "--dest", target,
            "--sync", "--verify", "--dry-run", "--quiet"]);
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(repo.GetSnapshotsForProject("Protected"));
        Assert.Equal(original, File.ReadAllBytes(db));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task WatchPreviewRejectsMissingDatabaseWithoutCreatingParent()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "absent", "vault.db");
        Assert.Equal(1, await Program.Main(["watch", "Missing", "--db", db, "--dry-run", "--quiet"]));
        Assert.False(Directory.Exists(Path.GetDirectoryName(db)));
    }
}
