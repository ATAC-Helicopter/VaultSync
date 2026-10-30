#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskWorkerInstallationManifestTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string[] Names = ["VaultSync.DiskWorker", "VaultSync.DiskWorker.dll",
        "VaultSync.DiskWorker.deps.json", "VaultSync.DiskWorker.runtimeconfig.json", "VaultSync.Core.dll",
        "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll"];
    private static JsonObject Manifest()
    {
        var files = new JsonArray();
        foreach (string name in Names.Order(StringComparer.Ordinal))
            files.Add(new JsonObject { ["name"] = name, ["size"] = 256, ["sha256"] = new string('a', 64),
                ["executable"] = name == "VaultSync.DiskWorker" });
        return new() { ["schemaVersion"] = 1, ["role"] = "read-only-inspection-v1", ["runtimeIdentifier"] = "linux-x64",
            ["sourceCommit"] = Commit, ["files"] = files };
    }
    private static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    [Fact]
    public void CompleteReadOnlyRoleKeepsExactCommitAndSortedClosure()
    {
        var result = DiskWorkerInstallationManifest.Decode(Bytes(Manifest()), Commit);
        Assert.Equal(Commit, result.SourceCommit);
        Assert.Equal(Names.Order(StringComparer.Ordinal), result.Files.Select(file => file.Name));
        Assert.Single(result.Files, file => file.Executable);
    }

    [Theory]
    [InlineData("schemaVersion")] [InlineData("role")] [InlineData("runtimeIdentifier")]
    [InlineData("sourceCommit")] [InlineData("files")]
    public void MissingFieldsCannotAuthorizeAWorker(string name)
    {
        var value = Manifest(); value.Remove(name);
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
    }

    [Theory]
    [InlineData("role", "restore-v1")] [InlineData("runtimeIdentifier", "linux-arm64")]
    [InlineData("sourceCommit", "ffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("schemaVersion", "1")]
    public void ForeignRoleRuntimeCommitAndSchemaTypesRefuse(string field, string replacement)
    {
        var value = Manifest(); value[field] = replacement;
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
    }

    [Theory]
    [InlineData("../file")] [InlineData("/file")] [InlineData("folder/file")] [InlineData("folder\\file")]
    [InlineData(".hidden")] [InlineData("worker.installation.json")] [InlineData("name\n")]
    public void FilesCannotSelectPathsOrIncludeTheirOwnManifest(string name)
    {
        var value = Manifest(); value["files"]![0]!["name"] = name;
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
    }

    [Theory]
    [InlineData(0L)] [InlineData(-1L)] [InlineData(67108865L)] [InlineData(long.MaxValue)]
    public void FileLengthsRefuseBeforeReadingFiles(long size)
    {
        var value = Manifest(); value["files"]![0]!["size"] = size;
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
    }

    [Fact]
    public void DuplicateUnsortedMissingAndOversizedClosuresRefuse()
    {
        foreach (string mode in new[] { "duplicate", "unsorted", "too-many", "too-large" }.Concat(Names))
        {
            var value = Manifest(); var files = (JsonArray)value["files"]!;
            if (mode == "duplicate") files.Insert(0, files[0]!.DeepClone());
            else if (mode == "unsorted") { var first = files[0]; files.RemoveAt(0); files.Add(first); }
            else if (mode == "too-many") while (files.Count <= 512) files.Add(files[0]!.DeepClone());
            else if (mode == "too-large") foreach (var file in files) file!["size"] = 67108864L;
            else { var found = files.First(file => file!["name"]!.GetValue<string>() == mode); files.Remove(found); }
            Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
        }
    }

    [Fact]
    public void MalformedKindsExtraFieldsDuplicateKeysAndHugeInputFailClosed()
    {
        var value = Manifest(); value["unexpected"] = true;
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(value), Commit));
        foreach (string text in new[] { "null", "[]", "{}", "{", "{\"schemaVersion\":1,\"schemaVersion\":1}" })
        {
            var error = Record.Exception(() => DiskWorkerInstallationManifest.Decode(Encoding.UTF8.GetBytes(text), Commit));
            Assert.True(error is JsonException or System.IO.InvalidDataException, error?.GetType().Name ?? "accepted");
        }
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(new byte[131073], Commit));
        var bad = Manifest(); bad["files"]![0]!["size"] = "256";
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(bad), Commit));
        bad = Manifest(); bad["files"]![0]!["executable"] = "false";
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(bad), Commit));
        bad = Manifest(); bad["files"]![0]!["sha256"] = new string('A', 64);
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(bad), Commit));
        bad = Manifest(); foreach (var file in (JsonArray)bad["files"]!) file!["executable"] = false;
        Assert.Throws<System.IO.InvalidDataException>(() => DiskWorkerInstallationManifest.Decode(Bytes(bad), Commit));
    }
}
