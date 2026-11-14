using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.CLI.Commands;
using VaultSync.Core.Config;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

[Collection("CLI presentation console")]
public sealed class DestinationCommandTests
{
    [Fact]
    public async Task EmptyLegacyJsonIsAnArray()
    {
        using var scope = new TestAppConfigScope();
        (int exitCode, string stdout) = await RunAsync("destinations", "--json");

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(0, json.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task VersionedListDoesNotPersistDefaultsOrExposeDestinationSecrets()
    {
        using var scope = new TestAppConfigScope();
        string configPath = Path.Combine(scope.ConfigDirectory, "appsettings.json");
        (int emptyCode, string emptyOutput) = await RunAsync("destinations", "--output", "json");
        Assert.Equal(0, emptyCode);
        Assert.False(File.Exists(configPath));
        using (JsonDocument empty = JsonDocument.Parse(emptyOutput))
            Assert.Equal(0, empty.RootElement.GetProperty("data").GetProperty("count").GetInt32());

        AppConfigStore.Save(new AppConfig
        {
            Backups = new BackupsConfig
            {
                Destinations = [new BackupDestination { Alias = "Offsite", Path = "/private/backup-target", IsOffsite = true,
                    CredentialName = "Private credential" }]
            },
            Network = new NetworkConfig
            {
                Credentials = [new NetworkCredentialProfile { Name = "Private credential", Password = "top-secret" }]
            }
        });
        (int exitCode, string stdout) = await RunAsync("destinations", "--output", "json");

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("destinations.list", json.RootElement.GetProperty("operation").GetString());
        JsonElement row = json.RootElement.GetProperty("data").GetProperty("destinations")[0];
        Assert.Equal("Offsite", row.GetProperty("alias").GetString());
        Assert.True(row.GetProperty("offsite").GetBoolean());
        Assert.False(row.GetProperty("accessibilityChecked").GetBoolean());
        Assert.DoesNotContain("/private/backup-target", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Private credential", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionedTestIsRejectedBeforeReadingConfiguration()
    {
        using var scope = new TestAppConfigScope();
        (int exitCode, string stdout) = await RunAsync("destinations", "--test", "--output", "json");

        Assert.Equal(2, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("invalid_options", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(scope.ConfigDirectory, "appsettings.json")));
    }

    [Fact]
    public async Task VersionedListRejectsDamagedConfigurationWithoutReturningDefaults()
    {
        using var scope = new TestAppConfigScope();
        File.WriteAllText(Path.Combine(scope.ConfigDirectory, "appsettings.json"), "{ private broken content");
        (int exitCode, string stdout) = await RunAsync("destinations", "--output", "json");

        Assert.Equal(1, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("config_unavailable", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("private broken content", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionedListUsesExistingBackupWhenPrimaryConfigurationIsDamaged()
    {
        using var scope = new TestAppConfigScope();
        var config = new AppConfig
        {
            Backups = new BackupsConfig { Destinations = [new BackupDestination { Alias = "Recovered setting" }] }
        };
        AppConfigStore.Save(config);
        AppConfigStore.Save(config);
        File.WriteAllText(Path.Combine(scope.ConfigDirectory, "appsettings.json"), "not JSON");

        (int exitCode, string stdout) = await RunAsync("destinations", "--output", "json");

        Assert.Equal(0, exitCode);
        using JsonDocument json = JsonDocument.Parse(stdout);
        Assert.Equal("Recovered setting", json.RootElement.GetProperty("data")
            .GetProperty("destinations")[0].GetProperty("alias").GetString());
        Assert.Equal("not JSON", File.ReadAllText(Path.Combine(scope.ConfigDirectory, "appsettings.json")));
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(params string[] args)
    {
        _ = Spectre.Console.AnsiConsole.Console;
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            int result = await Program.Main(args);
            return (result, output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [Theory]
    [InlineData(true, null, "Reachable")]
    [InlineData(false, "", "Unreachable")]
    [InlineData(true, "Mounted primary", "Mounted primary")]
    [InlineData(false, "Mount failed", "Mount failed")]
    public void ResolveDestinationMessage_PreservesDetailsOrProvidesFallback(
        bool reachable,
        string message,
        string expected)
    {
        Assert.Equal(expected, DestinationCommand.ResolveDestinationMessage(reachable, message));
    }

    [Theory]
    [InlineData(false, true, "Configured")]
    [InlineData(true, true, "Reachable")]
    [InlineData(true, false, "Mount failed")]
    public void ResolveTableDetail_UsesReachabilityOnlyForTestedRows(
        bool test,
        bool reachable,
        string expected)
    {
        var row = new DestinationInfo("Primary", "/backup", "Active", reachable, reachable ? "Configured" : "Mount failed");

        Assert.Equal(expected, DestinationCommand.ResolveTableDetail(row, test));
    }
}
