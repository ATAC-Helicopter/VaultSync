using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VaultSync.Core.Services;

public sealed record BuildInformation(
    int SchemaVersion,
    string Product,
    string Version,
    string ReleaseChannel,
    string SourceCommit,
    string Runtime,
    string RuntimeIdentifier,
    string Architecture,
    string OperatingSystem,
    string PackageKind,
    string UpdateSource,
    bool OfficialBuild,
    string SignatureStatus)
{
    public const string Unknown = "unknown";

    public string ToJson(bool indented = false) => JsonSerializer.Serialize(this, JsonOptions(indented));

    public string ToDisplayText() => string.Join(Environment.NewLine,
        $"Product: {Product}",
        $"Version: {Version}",
        $"Channel: {ReleaseChannel}",
        $"Commit: {SourceCommit}",
        $"Runtime: {Runtime}",
        $"Runtime identifier: {RuntimeIdentifier}",
        $"Architecture: {Architecture}",
        $"Operating system: {OperatingSystem}",
        $"Package: {PackageKind}",
        $"Updates: {UpdateSource}",
        $"Official build: {(OfficialBuild ? "yes" : "no")}",
        $"Signature: {SignatureStatus}");

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };
}

public sealed record BuildInformationOverrides(
    string? PackageKind = null,
    string? UpdateSource = null,
    string? ReleaseChannel = null,
    string? SignatureStatus = null,
    string? RuntimeIdentifier = null,
    string? Architecture = null,
    string? OperatingSystem = null);

public static class BuildInformationService
{
    private const string ProductName = "VaultSync";

    public static BuildInformation Create(Assembly assembly, BuildInformationOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        overrides ??= new BuildInformationOverrides();

        Dictionary<string, string> metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        string version = Normalize(informational.Split('+', 2)[0]);
        if (version == BuildInformation.Unknown)
            version = Normalize(assembly.GetName().Version?.ToString());

        string sourceCommit = Normalize(Metadata(metadata, "VaultSyncSourceCommit"));
        if (sourceCommit == BuildInformation.Unknown)
            sourceCommit = ExtractCommit(informational);

        string channel = Normalize(overrides.ReleaseChannel ?? Metadata(metadata, "VaultSyncReleaseChannel"));
        string packageKind = Normalize(overrides.PackageKind ?? Metadata(metadata, "VaultSyncPackageKind"));
        string updateSource = Normalize(overrides.UpdateSource ?? Metadata(metadata, "VaultSyncUpdateSource"));
        string signatureStatus = Normalize(overrides.SignatureStatus ?? Metadata(metadata, "VaultSyncSignatureStatus"));

        bool officialRequested = bool.TryParse(Metadata(metadata, "VaultSyncOfficialBuild"), out bool official) && official;
        bool officialBuild = officialRequested &&
            version != BuildInformation.Unknown &&
            channel != BuildInformation.Unknown &&
            sourceCommit != BuildInformation.Unknown &&
            packageKind != BuildInformation.Unknown;

        return new BuildInformation(
            SchemaVersion: 1,
            Product: ProductName,
            Version: version,
            ReleaseChannel: channel,
            SourceCommit: sourceCommit,
            Runtime: Normalize(RuntimeInformation.FrameworkDescription),
            RuntimeIdentifier: Normalize(overrides.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier),
            Architecture: Normalize(overrides.Architecture ?? RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()),
            OperatingSystem: Normalize(overrides.OperatingSystem ?? RuntimeInformation.OSDescription),
            PackageKind: packageKind,
            UpdateSource: updateSource,
            OfficialBuild: officialBuild,
            SignatureStatus: signatureStatus);
    }

    private static string Metadata(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out string? value) ? value : string.Empty;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? BuildInformation.Unknown : value.Trim();

    private static string ExtractCommit(string informational)
    {
        int separator = informational.LastIndexOf('+');
        if (separator < 0 || separator == informational.Length - 1)
            return BuildInformation.Unknown;

        string candidate = informational[(separator + 1)..].Trim();
        return candidate.Length is >= 7 and <= 64 && candidate.All(Uri.IsHexDigit)
            ? candidate
            : BuildInformation.Unknown;
    }
}
