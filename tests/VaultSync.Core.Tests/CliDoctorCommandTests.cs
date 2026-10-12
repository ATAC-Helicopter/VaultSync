using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliDoctorCommandTests
{
    [Fact]
    public async Task VersionedDoctorReportsChecksWithoutCreatingDatabaseOrExposingPaths()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "doctor.db");
        _ = AnsiConsole.Console;
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        int result;
        try
        {
            Console.SetOut(output);
            result = await Program.Main(["doctor", "--db", database, "--output", "json"]);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(2, result);
        Assert.False(File.Exists(database));
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Equal("doctor", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("diagnostics_failed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(json.RootElement.GetProperty("error").GetProperty("details").GetProperty("failedCount").GetInt32() > 0);
        Assert.DoesNotContain(root.Path, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionedDoctorReportsPassingChecksForExistingProject()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "doctor.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        SqliteRepository repository = TestRepository.Create(database);
        TestRepository.AddProject(repository, "Healthy", source);
        _ = AnsiConsole.Console;
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        int result;
        try
        {
            Console.SetOut(output);
            result = await Program.Main(["doctor", "--db", database, "--output", "json"]);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(0, result);
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("passedCount").GetInt32() >= 3);
        Assert.Equal(0, data.GetProperty("failedCount").GetInt32());
        Assert.DoesNotContain(source, output.ToString(), StringComparison.Ordinal);
    }

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
