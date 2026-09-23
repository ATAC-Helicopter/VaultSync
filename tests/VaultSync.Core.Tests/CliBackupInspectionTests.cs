using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using VaultSync.CLI;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class CliBackupInspectionTests
{
    [Fact]
    public async Task ListAndShowReturnScopedRecordsWithoutPathsOrPayloadClaims()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Photos", root.Path);
        int otherId = TestRepository.AddProject(repository, "Other", root.Path);
        int snapshotId = repository.CreateSnapshot(projectId, 1, 12);
        int foreignSnapshotId = repository.CreateSnapshot(otherId, 1, 9);
        int backupId = repository.CreateBackup(projectId, snapshotId, "manual", 12,
            "private-backup-path", root.Path, "Secret destination alias", isProtected: true);
        int foreignId = repository.CreateBackup(otherId, foreignSnapshotId, "manual", 9,
            "foreign-private-path", root.Path, "Other destination");

        var listing = await RunAsync("backups", "list", "Photos", "--db", database, "--limit", "1", "--output", "json");
        Assert.Equal(0, listing.Code);
        Assert.Empty(listing.Error);
        AssertEnvelope(listing.Json, "backups.list", 0);
        JsonElement data = listing.Json.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(1, data.GetProperty("totalCount").GetInt32());
        Assert.Equal(backupId, data.GetProperty("backups")[0].GetProperty("id").GetInt32());
        Assert.False(data.GetProperty("backups")[0].GetProperty("payloadChecked").GetBoolean());
        Assert.DoesNotContain("private-backup-path", listing.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret destination alias", listing.Output, StringComparison.Ordinal);

        var detail = await RunAsync("backups", "show", "Photos", "--id", backupId.ToString(),
            "--db", database, "--output", "json");
        AssertEnvelope(detail.Json, "backups.show", 0);
        Assert.Equal(snapshotId, detail.Json.GetProperty("data").GetProperty("backup").GetProperty("snapshotId").GetInt32());
        Assert.True(detail.Json.GetProperty("data").GetProperty("backup").GetProperty("isProtected").GetBoolean());
        Assert.DoesNotContain("private-backup-path", detail.Output, StringComparison.Ordinal);

        var foreign = await RunAsync("backups", "show", "Photos", "--id", foreignId.ToString(),
            "--db", database, "--output", "json");
        AssertEnvelope(foreign.Json, "backups.show", 2);
        Assert.Equal("backup_not_found", foreign.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("foreign-private-path", foreign.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list", "--limit", "0")]
    [InlineData("show", "--id", "0")]
    [InlineData("show", "--unknown", "1")]
    public async Task InvalidRequestsDoNotCreateDatabase(string action, string option, string value)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        var result = await RunAsync("backups", action, "Photos", option, value,
            "--db", database, "--output", "json");
        AssertEnvelope(result.Json, $"backups.{action}", 2);
        Assert.Equal("invalid_options", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task MissingStoreReturnsVersionedFailureWithoutInitializingIt()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        var result = await RunAsync("backups", "list", "Photos", "--db", database, "--output", "json");
        AssertEnvelope(result.Json, "backups.list", 1);
        Assert.Equal("repository_unavailable", result.Json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    [Fact]
    public async Task VerifyChecksRecordedFolderBytesAndReportsTampering()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        string backupRoot = Directory.CreateDirectory(Path.Combine(root.Path, "backups")).FullName;
        string backupFolder = Directory.CreateDirectory(Path.Combine(backupRoot, "point-1")).FullName;
        string payload = Path.Combine(backupFolder, "state.txt");
        File.WriteAllText(payload, "recorded");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Photos", root.Path);
        int snapshotId = repository.CreateSnapshot(projectId, 1, 8);
        repository.InsertFiles(snapshotId,
        [
            new FileEntry("state.txt", 8, DateTime.UtcNow,
                Convert.ToHexString(SHA256.HashData("recorded"u8.ToArray())))
        ]);
        int backupId = repository.CreateBackup(projectId, snapshotId, "manual", 8,
            "point-1", backupRoot, "Test");

        string[] args = ["backups", "verify", "Photos", "--id", backupId.ToString(),
            "--db", database, "--output", "json"];
        var valid = await RunAsync(args);
        AssertEnvelope(valid.Json, "backups.verify", 0);
        Assert.Empty(valid.Error);
        Assert.Equal(1, valid.Json.GetProperty("data").GetProperty("checkedFiles").GetInt32());
        Assert.Equal(0, valid.Json.GetProperty("data").GetProperty("failedFiles").GetInt32());

        File.WriteAllText(payload, "tampered");
        var tampered = await RunAsync(args);
        AssertEnvelope(tampered.Json, "backups.verify", 1);
        Assert.Equal("verification_failed", tampered.Json.GetProperty("error").GetProperty("code").GetString());
        JsonElement details = tampered.Json.GetProperty("error").GetProperty("details");
        Assert.Equal(1, details.GetProperty("failedFiles").GetInt32());
        Assert.Equal("hash_mismatch", details.GetProperty("failures")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyRejectsBackupWhoseSnapshotBelongsToAnotherProject()
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Photos", root.Path);
        int otherId = TestRepository.AddProject(repository, "Other", root.Path);
        int otherSnapshot = repository.CreateSnapshot(otherId, 1, 4);
        int backupId = repository.CreateBackup(projectId, otherSnapshot, "manual", 4,
            "point-1", root.Path, "Test");

        var result = await RunAsync("backups", "verify", "Photos", "--id", backupId.ToString(),
            "--db", database, "--output", "json");
        AssertEnvelope(result.Json, "backups.verify", 2);
        Assert.Equal("snapshot_not_found", result.Json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("incremental", false)]
    [InlineData("full", true)]
    public async Task VerifyRejectsFormatsWithoutAQualifiedHashCheck(string mode, bool encrypted)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "vault.db");
        SqliteRepository repository = TestRepository.Create(database);
        int projectId = TestRepository.AddProject(repository, "Photos", root.Path);
        int snapshotId = repository.CreateSnapshot(projectId, 1, 4);
        int backupId = repository.CreateBackup(projectId, snapshotId, "manual", 4,
            "point-1", root.Path, "Test", backupMode: mode, isEncrypted: encrypted);

        var result = await RunAsync("backups", "verify", "Photos", "--id", backupId.ToString(),
            "--db", database, "--output", "json");
        AssertEnvelope(result.Json, "backups.verify", 2);
        Assert.Equal("unsupported_backup_format", result.Json.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("--id", "0")]
    [InlineData("--limit", "0")]
    public async Task InvalidVerifyDoesNotCreateDatabase(string option, string value)
    {
        using var root = new TempDirectory();
        string database = Path.Combine(root.Path, "missing", "vault.db");
        var result = await RunAsync("backups", "verify", "Photos", option, value,
            "--db", database, "--output", "json");
        AssertEnvelope(result.Json, "backups.verify", 2);
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
    }

    private static void AssertEnvelope(JsonElement json, string operation, int exitCode)
    {
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(operation, json.GetProperty("operation").GetString());
        Assert.Equal(exitCode, json.GetProperty("exitCode").GetInt32());
    }

    private static async Task<(int Code, JsonElement Json, string Output, string Error)> RunAsync(params string[] args)
    {
        _ = Spectre.Console.AnsiConsole.Console;
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
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
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return (code, document.RootElement.Clone(), output.ToString(), error.ToString());
    }
}
