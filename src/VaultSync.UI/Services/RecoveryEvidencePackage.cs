using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultSync.UI.Services;

internal sealed record RecoveryEvidencePackageValidationResult(
    bool IsValid,
    string Message,
    IReadOnlyList<string> Files);

internal static class RecoveryEvidencePackage
{
    private const int SchemaVersion = 1;
    private const long MaximumEntryBytes = 16 * 1024 * 1024;
    private static readonly DateTimeOffset StableZipTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly string[] RequiredEntries =
    [
        "recovery-evidence.json",
        "recovery-report.md",
        "manifest.json",
        "SHA256SUMS"
    ];

    public static string Export(
        RecoveryReportSnapshot snapshot,
        RecoveryReportLabels labels,
        string? exportRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(exportRoot)
            ? RecoveryReportExporter.GetDefaultExportDirectory()
            : exportRoot;
        Directory.CreateDirectory(root);

        byte[] evidence = SerializeEvidence(snapshot);
        byte[] report = Encoding.UTF8.GetBytes(RecoveryReportExporter.BuildMarkdown(snapshot, labels));
        var content = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["recovery-evidence.json"] = evidence,
            ["recovery-report.md"] = report
        };
        byte[] manifest = SerializeManifest(snapshot, content);
        content["manifest.json"] = manifest;
        content["SHA256SUMS"] = BuildChecksumFile(content);

        string path = RecoveryReportExporter.EnsureUniquePath(Path.Combine(
            root,
            $"VaultSync-Recovery-Evidence-{snapshot.GeneratedAt:yyyyMMdd-HHmmss}.zip"));
        using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach ((string name, byte[] bytes) in content)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = StableZipTimestamp;
                using Stream target = entry.Open();
                target.Write(bytes);
            }
        }

        return path;
    }

    public static RecoveryEvidencePackageValidationResult Validate(string packagePath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            string[] names = [.. archive.Entries.Select(entry => entry.FullName)];
            if (names.Length != names.Distinct(StringComparer.Ordinal).Count())
                return Invalid("The package contains duplicate entries.", names);
            if (names.Any(IsUnsafeEntryName))
                return Invalid("The package contains an unsafe entry path.", names);
            if (!names.Order(StringComparer.Ordinal).SequenceEqual(RequiredEntries.Order(StringComparer.Ordinal)))
                return Invalid("The package is missing a required file or contains an unexpected file.", names);
            if (archive.Entries.Any(entry => entry.Length > MaximumEntryBytes))
                return Invalid("A package entry exceeds the supported size limit.", names);

            Dictionary<string, byte[]> files = archive.Entries.ToDictionary(
                entry => entry.FullName,
                ReadEntry,
                StringComparer.Ordinal);
            if (!HasSupportedSchema(files["recovery-evidence.json"]) ||
                !HasSupportedSchema(files["manifest.json"]))
            {
                return Invalid("The package uses an unsupported schema version.", names);
            }

            EvidenceManifest? manifest = JsonSerializer.Deserialize<EvidenceManifest>(
                files["manifest.json"], JsonOptions);
            if (manifest is null || manifest.Files is null || manifest.Files.Count != 2 ||
                manifest.Files.Select(item => item.Path).Distinct(StringComparer.Ordinal).Count() != 2 ||
                !manifest.Files.Select(item => item.Path).Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { "recovery-evidence.json", "recovery-report.md" }, StringComparer.Ordinal))
                return Invalid("The package manifest is invalid.", names);
            foreach (EvidenceManifestFile item in manifest.Files)
            {
                if (!files.TryGetValue(item.Path, out byte[]? bytes) ||
                    bytes.LongLength != item.Bytes ||
                    !string.Equals(Hash(bytes), item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid($"Checksum validation failed for {item.Path}.", names);
                }
            }

            string expectedSums = Encoding.UTF8.GetString(BuildChecksumFile(
                new SortedDictionary<string, byte[]>(files
                    .Where(pair => !string.Equals(pair.Key, "SHA256SUMS", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal)));
            string actualSums = Encoding.UTF8.GetString(files["SHA256SUMS"]);
            if (!string.Equals(actualSums, expectedSums, StringComparison.Ordinal))
                return Invalid("The package checksum index is invalid.", names);

            return new RecoveryEvidencePackageValidationResult(true, "Package checksums are valid.", names);
        }
        catch (InvalidDataException ex)
        {
            return Invalid($"The package is not a valid ZIP archive: {ex.Message}", []);
        }
        catch (IOException ex)
        {
            return Invalid($"The package could not be read: {ex.Message}", []);
        }
        catch (JsonException ex)
        {
            return Invalid($"The package contains invalid JSON: {ex.Message}", []);
        }
    }

    internal static byte[] SerializeEvidence(RecoveryReportSnapshot snapshot)
    {
        var document = new RecoveryEvidenceDocument(
            SchemaVersion,
            snapshot.GeneratedAt.ToUniversalTime(),
            snapshot.AppVersion,
            snapshot.SourceIdentity,
            snapshot.Build,
            new RecoveryEvidenceReadiness(
                snapshot.ReadinessPercent,
                snapshot.ReadinessBand,
                snapshot.ProjectCount,
                snapshot.ReadyCount,
                snapshot.AttentionCount,
                snapshot.RiskCount,
                snapshot.UnavailableCount),
            new RecoveryEvidenceCoverage(
                snapshot.Coverage24Hours,
                snapshot.Coverage7Days,
                snapshot.Coverage30Days,
                snapshot.Coverage90Days,
                snapshot.ThreeTwoOneReadyCount,
                snapshot.DrilledProjectCount,
                snapshot.PassedDrillCount,
                snapshot.ProtectedPointCount),
            snapshot.Projects.Select(project => new RecoveryEvidenceProject(
                project.RepositoryIdentity,
                project.ProjectName,
                project.Status,
                project.Score,
                project.Reason,
                project.Copies,
                project.Media,
                project.Offsite,
                project.LastDrill,
                project.ConfidenceEvidence?.Any(item =>
                    string.Equals(item.Kind, "Credential", StringComparison.Ordinal)) == true,
                project.ConfidenceEvidence?.Select(item => new RecoveryEvidenceFreshness(
                    item.Kind,
                    item.Basis,
                    item.Status,
                    item.Code,
                    item.ObservedAtUtc?.ToUniversalTime())).ToArray() ?? [],
                project.Evidence?.Select(item => new RecoveryEvidenceItem(
                    item.Code,
                    item.Status,
                    item.Detail,
                    item.EvidenceId,
                    RecoveryReportExporter.RedactEvidencePath(item.Path))).ToArray() ?? [])).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static byte[] SerializeManifest(
        RecoveryReportSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> files)
    {
        var manifest = new EvidenceManifest(
            SchemaVersion,
            "VaultSync recovery evidence package",
            snapshot.GeneratedAt.ToUniversalTime(),
            files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new EvidenceManifestFile(pair.Key, pair.Value.LongLength, Hash(pair.Value)))
                .ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
    }

    private static byte[] BuildChecksumFile(IReadOnlyDictionary<string, byte[]> files)
    {
        string text = string.Concat(files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Hash(pair.Value)}  {pair.Key}\n"));
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream source = entry.Open();
        using var target = new MemoryStream();
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumEntryBytes)
                throw new InvalidDataException("A package entry exceeds the supported size limit.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    private static bool HasSupportedSchema(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("schemaVersion", out JsonElement version) &&
               version.ValueKind == JsonValueKind.Number &&
               version.GetInt32() == SchemaVersion;
    }

    private static bool IsUnsafeEntryName(string name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.Contains('\\', StringComparison.Ordinal) ||
        name.StartsWith("/", StringComparison.Ordinal) ||
        name.Split('/').Any(part => part is "" or "." or "..");

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static RecoveryEvidencePackageValidationResult Invalid(string message, IReadOnlyList<string> files) =>
        new(false, message, files);

    private sealed record RecoveryEvidenceDocument(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string AppVersion,
        string SourceIdentity,
        VaultSync.Core.Services.BuildInformation? Build,
        RecoveryEvidenceReadiness Readiness,
        RecoveryEvidenceCoverage Coverage,
        IReadOnlyList<RecoveryEvidenceProject> Projects);
    private sealed record RecoveryEvidenceReadiness(
        int Percent, string Band, int Projects, int Ready, int Attention, int Risk, int Unavailable);
    private sealed record RecoveryEvidenceCoverage(
        int Within24Hours, int Within7Days, int Within30Days, int Within90Days,
        int ThreeTwoOneReady, int DrillsRun, int DrillsPassed, int ProtectedPoints);
    private sealed record RecoveryEvidenceProject(
        string RepositoryIdentity, string Name, string Status, int Score, string Reason,
        string Copies, string Media, string Offsite, string LastDrill,
        bool HasEncryptedRecoveryPointEvidence,
        IReadOnlyList<RecoveryEvidenceFreshness> ConfidenceEvidence,
        IReadOnlyList<RecoveryEvidenceItem> Evidence);
    private sealed record RecoveryEvidenceFreshness(
        string Kind, string Basis, string Status, string Code, DateTimeOffset? ObservedAtUtc);
    private sealed record RecoveryEvidenceItem(
        string Code, string Status, string Detail, string EvidenceId, string Path);
    private sealed record EvidenceManifest(
        int SchemaVersion, string PackageType, DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<EvidenceManifestFile> Files);
    private sealed record EvidenceManifestFile(string Path, long Bytes, string Sha256);
}
