using System.Text.Json;

namespace VaultSync.Core.Services;

internal sealed record DiskWorkerInstallationFile(string Name, long Size, string Sha256, bool Executable);
internal sealed record DiskWorkerInstallationManifest(string SourceCommit, IReadOnlyList<DiskWorkerInstallationFile> Files)
{
    internal const int MaximumBytes = 128 * 1024, MaximumFiles = 512;
    internal const long MaximumFileBytes = 64L * 1024 * 1024, MaximumTotalBytes = 512L * 1024 * 1024;
    internal const string FileName = "worker.installation.json";
    internal const string Role = "read-only-inspection-v1";
    internal static readonly IReadOnlyList<string> RequiredFiles = Array.AsReadOnly(new[] {"VaultSync.DiskWorker", "VaultSync.DiskWorker.dll",
        "VaultSync.DiskWorker.deps.json", "VaultSync.DiskWorker.runtimeconfig.json", "VaultSync.Core.dll",
        "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll" });

    internal static DiskWorkerInstallationManifest Decode(byte[] bytes, string expectedCommit)
    {
        if (bytes.Length is 0 or > MaximumBytes || expectedCommit.Length != 40 || !Hex(expectedCommit))
            throw new InvalidDataException("worker-installation.manifest-bound");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            { MaxDepth = 4, AllowDuplicateProperties = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        Keys(root, "schemaVersion", "role", "runtimeIdentifier", "sourceCommit", "files");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number || !root.GetProperty("schemaVersion").TryGetInt32(out int version) || version != 1 ||
            Text(root, "role") != Role || Text(root, "runtimeIdentifier") != "linux-x64" ||
            Text(root, "sourceCommit") != expectedCommit)
            throw new InvalidDataException("worker-installation.build-role");
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > MaximumFiles)
            throw new InvalidDataException("worker-installation.file-count");
        var result = new List<DiskWorkerInstallationFile>(files.GetArrayLength());
        string? previous = null; long total = 0;
        foreach (var item in files.EnumerateArray())
        {
            Keys(item, "name", "size", "sha256", "executable");
            string name = Text(item, "name"), hash = Text(item, "sha256");
            var executable = item.GetProperty("executable");
            if (!ValidName(name) || name == FileName || previous is not null && string.CompareOrdinal(previous, name) >= 0 ||
                item.GetProperty("size").ValueKind != JsonValueKind.Number || !item.GetProperty("size").TryGetInt64(out long size) || size is <= 0 or > MaximumFileBytes ||
                hash.Length != 64 || !Hex(hash) || executable.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                total > MaximumTotalBytes - size)
                throw new InvalidDataException("worker-installation.file-record");
            total += size; previous = name;
            result.Add(new(name, size, hash, executable.GetBoolean()));
        }
        if (RequiredFiles.Any(name => !result.Any(file => file.Name == name)) ||
            !result.Single(file => file.Name == "VaultSync.DiskWorker").Executable)
            throw new InvalidDataException("worker-installation.incomplete-runtime");
        return new(expectedCommit, result.AsReadOnly());
    }

    internal static bool ValidName(string name) => name.Length is > 0 and <= 128 && name is not ("." or "..") &&
        name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static bool Hex(string value) => value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Text(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.String
        ? element.GetProperty(name).GetString()! : throw new InvalidDataException("worker-installation.string");
    private static void Keys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != expected.Length ||
            element.EnumerateObject().Any(property => !expected.Contains(property.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("worker-installation.fields");
    }
}
