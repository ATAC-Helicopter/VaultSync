using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VaultSync.UI.Services
{
    internal sealed record PublishedReleaseAsset(string Name, string? DownloadUrl, long Size, string? Digest);

    internal sealed class ReleaseManifestAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("platform")]
        public string? Platform { get; init; }

        [JsonPropertyName("architecture")]
        public string? Architecture { get; init; }

        [JsonPropertyName("packageKind")]
        public string? PackageKind { get; init; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; init; }
    }

    internal static class ReleaseManifestVerifier
    {
        internal const string ManifestName = "vaultsync-release-manifest.json";
        private const string Repository = "ATAC-Helicopter/VaultSync";
        private const int SchemaVersion = 1;
        private static readonly Regex s_versionPattern = new(
            "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> s_platforms = ["windows", "macos", "linux"];
        private static readonly HashSet<string> s_architectures = ["x64", "arm64"];
        private static readonly HashSet<string> s_packageKinds =
        [
            "installer", "store-upload", "disk-image", "archive", "debian-package",
            "appimage", "patch-manifest", "patch-archive"
        ];

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        internal static bool TryValidate(
            string json,
            string releaseTag,
            bool prerelease,
            IReadOnlyCollection<PublishedReleaseAsset> publishedAssets,
            out IReadOnlyDictionary<string, ReleaseManifestAsset> assets)
        {
            assets = new Dictionary<string, ReleaseManifestAsset>();
            ReleaseManifestDocument? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ReleaseManifestDocument>(json, s_jsonOptions);
            }
            catch (JsonException)
            {
                return false;
            }

            if (!HasValidIdentity(manifest, releaseTag, prerelease) || manifest!.Assets is not { Count: > 0 })
                return false;

            Dictionary<string, PublishedReleaseAsset>? published = IndexPublishedAssets(publishedAssets);
            if (published is null || published.Count != manifest.Assets.Count)
                return false;

            var verified = new Dictionary<string, ReleaseManifestAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (ReleaseManifestAsset asset in manifest.Assets)
            {
                if (!TryValidateAsset(asset, releaseTag, published, out string name) || !verified.TryAdd(name, asset))
                    return false;
            }

            if (verified.Count != published.Count || verified.Keys.Any(name => !published.ContainsKey(name)))
                return false;

            assets = verified;
            return true;
        }

        private static bool HasValidIdentity(ReleaseManifestDocument? manifest, string releaseTag, bool prerelease)
        {
            ReleaseManifestIdentity? release = manifest?.Release;
            string expectedChannel = prerelease ? "beta" : "stable";
            if (manifest?.SchemaVersion != SchemaVersion ||
                release is null ||
                release.Version is null ||
                !s_versionPattern.IsMatch(release.Version) ||
                !HasValidPredecessors(release))
            {
                return false;
            }

            return
                   string.Equals(release.Repository, Repository, StringComparison.Ordinal) &&
                   string.Equals(release.Tag, releaseTag, StringComparison.Ordinal) &&
                   string.Equals($"v{release.Version}", releaseTag, StringComparison.Ordinal) &&
                   string.Equals(release.Channel, expectedChannel, StringComparison.Ordinal) &&
                   (prerelease == release.Version.Contains('-', StringComparison.Ordinal)) &&
                   IsLowerHex(release.Commit, 40);
        }

        private static bool HasValidPredecessors(ReleaseManifestIdentity release)
        {
            if (release.CompatiblePredecessors is not { Count: > 0 })
                return false;

            var unique = new HashSet<string>(StringComparer.Ordinal);
            return release.CompatiblePredecessors.All(version =>
                s_versionPattern.IsMatch(version) &&
                !string.Equals(version, release.Version, StringComparison.Ordinal) &&
                unique.Add(version));
        }

        private static Dictionary<string, PublishedReleaseAsset>? IndexPublishedAssets(
            IReadOnlyCollection<PublishedReleaseAsset> publishedAssets)
        {
            var indexed = new Dictionary<string, PublishedReleaseAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (PublishedReleaseAsset asset in publishedAssets)
            {
                if (string.Equals(asset.Name, ManifestName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(asset.Name) || !indexed.TryAdd(asset.Name, asset))
                    return null;
            }
            return indexed;
        }

        private static bool TryValidateAsset(
            ReleaseManifestAsset asset,
            string releaseTag,
            IReadOnlyDictionary<string, PublishedReleaseAsset> published,
            out string name)
        {
            name = asset.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) ||
                name.IndexOfAny(['/', '\\']) >= 0 ||
                asset.SizeBytes <= 0 ||
                !IsLowerHex(asset.Sha256, 64) ||
                asset.Platform is null || !s_platforms.Contains(asset.Platform) ||
                asset.Architecture is null || !s_architectures.Contains(asset.Architecture) ||
                asset.PackageKind is null || !s_packageKinds.Contains(asset.PackageKind) ||
                !published.TryGetValue(name, out PublishedReleaseAsset? publishedAsset))
            {
                return false;
            }

            string expectedUrl = $"https://github.com/{Repository}/releases/download/{releaseTag}/{Uri.EscapeDataString(name)}";
            string? digest = GitHubUpdateService.TryParseSha256Digest(publishedAsset.Digest);
            return string.Equals(asset.DownloadUrl, expectedUrl, StringComparison.Ordinal) &&
                   string.Equals(publishedAsset.DownloadUrl, expectedUrl, StringComparison.Ordinal) &&
                   publishedAsset.Size == asset.SizeBytes &&
                   string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowerHex(string? value, int length) =>
            value is not null &&
            value.Length == length &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

        private sealed class ReleaseManifestDocument
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; init; }

            [JsonPropertyName("release")]
            public ReleaseManifestIdentity? Release { get; init; }

            [JsonPropertyName("assets")]
            public List<ReleaseManifestAsset>? Assets { get; init; }
        }

        private sealed class ReleaseManifestIdentity
        {
            [JsonPropertyName("version")]
            public string? Version { get; init; }

            [JsonPropertyName("channel")]
            public string? Channel { get; init; }

            [JsonPropertyName("tag")]
            public string? Tag { get; init; }

            [JsonPropertyName("commit")]
            public string? Commit { get; init; }

            [JsonPropertyName("repository")]
            public string? Repository { get; init; }

            [JsonPropertyName("compatiblePredecessors")]
            public List<string>? CompatiblePredecessors { get; init; }
        }
    }
}
