using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliDoctorCommandTests
{
    [Fact]
    public async Task DoctorPreservesExistingProbeNamedUserFiles()
    {
        using var root = new TempDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(root.Path, "destination")).FullName;
        string databaseSentinel = Path.Combine(root.Path, ".vaultsync_write_test");
        string destinationSentinel = Path.Combine(destination, ".vaultsync_write_test");
        File.WriteAllText(databaseSentinel, "existing database folder data");
        File.WriteAllText(destinationSentinel, "existing destination data");

        await Program.Main(["doctor", "--db", Path.Combine(root.Path, "doctor.db"),
            "--check-dest", destination, "--quiet"]).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(File.Exists(databaseSentinel), "Doctor removed an existing user file in the database folder");
        Assert.True(File.Exists(destinationSentinel), "Doctor removed an existing destination user file");
        Assert.Equal("existing database folder data", File.ReadAllText(databaseSentinel));
        Assert.Equal("existing destination data", File.ReadAllText(destinationSentinel));
        Assert.Empty(Directory.EnumerateFiles(root.Path, ".vaultsync_write_test_*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DoctorReportsValidProjectAndDestinationWithoutLeavingProbeFiles()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "doctor.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "Doctor [markup] project", source);
        string destination = Path.Combine(root.Path, "destination");

        int result = await Program.Main(["doctor", "--db", database, "--check-dest", destination])
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, result);
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(root.Path, ".vaultsync_write_test*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DoctorReportsMissingProjectPath()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "doctor.db");
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "Missing [project]", Path.Combine(root.Path, "absent"));

        int result = await Program.Main(["doctor", "--db", database]).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, result);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "absent")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DoctorReportsBlockedPathsWithoutChangingExistingFiles(bool blockDatabase)
    {
        using var root = new TempDirectory();
        string occupied = Path.Combine(root.Path, "occupied");
        File.WriteAllText(occupied, "existing user file");
        string database = blockDatabase ? Path.Combine(occupied, "doctor.db") : Path.Combine(root.Path, "doctor.db");
        string destination = blockDatabase ? Path.Combine(root.Path, "destination") : occupied;

        int result = await Program.Main(["doctor", "--db", database, "--check-dest", destination])
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, result);
        Assert.Equal("existing user file", File.ReadAllText(occupied));
    }
}
