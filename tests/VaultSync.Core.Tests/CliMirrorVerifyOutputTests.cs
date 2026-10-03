using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using VaultSync.CLI;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliMirrorVerifyOutputTests
{
    [Fact]
    public async Task MirrorPreviewAndVerifyReturnOneVersionedResultWithoutChangingTarget()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "vault.db");
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        string target = Path.Combine(root.Path, "target");
        File.WriteAllText(Path.Combine(source, "state.txt"), "state");
        Assert.Equal(0, await Program.Main(["projects", "add", "Output project", source, "--db", db, "--quiet"]));
        Assert.Equal(0, await Program.Main(["snapshots", "create", "Output project", "--db", db, "--quiet"]));

        var preview = await RunJsonAsync("mirror", "Output project", target, "--db", db, "--dry-run", "--output", "json");
        Assert.Equal(0, preview.Code);
        Assert.Equal("mirror", preview.Json.GetProperty("operation").GetString());
        Assert.True(preview.Json.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.False(preview.Json.GetProperty("data").GetProperty("recordedBackup").GetBoolean());
        Assert.False(Directory.Exists(target));

        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(source, "state.txt"), Path.Combine(target, "state.txt"));
        var valid = await RunJsonAsync("verify", "Output project", target, "--db", db, "--full", "--output", "json");
        Assert.Equal(0, valid.Code);
        Assert.Equal(1, valid.Json.GetProperty("data").GetProperty("checkedFiles").GetInt32());

        File.WriteAllText(Path.Combine(target, "state.txt"), "changed");
        var mismatch = await RunJsonAsync("verify", "Output project", target, "--db", db, "--full", "--output", "json");
        Assert.Equal(2, mismatch.Code);
        Assert.Equal("error", mismatch.Json.GetProperty("status").GetString());
        Assert.Equal("verification_failed", mismatch.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, mismatch.Json.GetProperty("error").GetProperty("details").GetProperty("failureCount").GetInt32());
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("sync")]
    [InlineData("verify")]
    public async Task VersionedTransferCommandsRejectMissingStore(string command)
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "absent", "vault.db");
        var result = await RunJsonAsync(command, "Missing", Path.Combine(root.Path, "target"), "--db", db, "--output", "json");
        Assert.Equal(1, result.Code);
        Assert.Equal("repository_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(db)));
    }

    [Fact]
    public async Task VersionedVerifyRejectsInvalidPercentBeforeDatabaseAccess()
    {
        using var root = new TempDirectory();
        string db = Path.Combine(root.Path, "absent", "vault.db");
        var result = await RunJsonAsync("verify", "Missing", root.Path, "--db", db,
            "--percent", "0", "--output", "json");
        Assert.Equal(2, result.Code);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(db)));
    }

    private static async Task<(int Code, JsonElement Json)> RunJsonAsync(params string[] args)
    {
        _ = AnsiConsole.Console;
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            int code = await Program.Main(args);
            using JsonDocument json = JsonDocument.Parse(output.ToString());
            Assert.Equal(code, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            return (code, json.RootElement.Clone());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
