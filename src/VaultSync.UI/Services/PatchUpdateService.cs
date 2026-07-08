using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services
{
    public sealed class PatchFileEntry
    {
        [JsonPropertyName("path")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public sealed class PatchManifest
    {
        [JsonPropertyName("previousVersion")]
        public string PreviousVersion { get; set; } = string.Empty;

        [JsonPropertyName("baseVersions")]
        public List<string> BaseVersions { get; set; } = [];

        [JsonPropertyName("targetVersion")]
        public string TargetVersion { get; set; } = string.Empty;

        [JsonPropertyName("archiveSha256")]
        public string ArchiveSha256 { get; set; } = string.Empty;

        [JsonPropertyName("archiveSize")]
        public long ArchiveSize { get; set; }

        [JsonPropertyName("files")]
        public List<PatchFileEntry> Files { get; set; } = [];
    }

    public sealed class PatchPlan
    {
        public PatchManifest Manifest { get; }
        public Uri ArchiveUrl { get; }
        public string ArchiveName { get; }

        public PatchPlan(PatchManifest manifest, Uri archiveUrl, string archiveName)
        {
            Manifest = manifest;
            ArchiveUrl = archiveUrl;
            ArchiveName = archiveName;
        }
    }

    public sealed class PatchPreflightResult
    {
        public PatchPreflightResult(
            bool eligible,
            bool requiresInstaller,
            string statusCode,
            string message,
            PatchPlan? plan,
            PatchManifest? manifest,
            bool hasManifest,
            bool hasArchive,
            bool hasInstaller)
        {
            Eligible = eligible;
            RequiresInstaller = requiresInstaller;
            StatusCode = statusCode;
            Message = message;
            Plan = plan;
            Manifest = manifest;
            HasManifest = hasManifest;
            HasArchive = hasArchive;
            HasInstaller = hasInstaller;
        }

        public bool Eligible { get; }
        public bool RequiresInstaller { get; }
        public string StatusCode { get; }
        public string Message { get; }
        public PatchPlan? Plan { get; }
        public PatchManifest? Manifest { get; }
        public bool HasManifest { get; }
        public bool HasArchive { get; }
        public bool HasInstaller { get; }
    }

    public sealed class PatchUpdateService
    {
        private const string InvalidBaseAllowlistStatus = "manifest-invalid-base-allowlist";
        private static readonly HttpClient s_httpClient = CreateHttpClient();
        private static readonly TimeSpan s_manifestCacheWindow = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, (PatchManifest Manifest, DateTimeOffset FetchedAt)> s_manifestCache =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<PatchPlan?> PreparePatchAsync(
            UpdateCheckResult updateResult,
            string currentVersion,
            CancellationToken cancellationToken)
        {
            PatchPreflightResult preflight = await PreflightPatchAsync(updateResult, currentVersion, cancellationToken);
            return preflight.Eligible ? preflight.Plan : null;
        }

        public static async Task<PatchPreflightResult> PreflightPatchAsync(
            UpdateCheckResult updateResult,
            string currentVersion,
            CancellationToken cancellationToken)
        {
            bool hasManifest = !string.IsNullOrWhiteSpace(updateResult.PatchManifestUrl);
            bool hasArchive = updateResult.PatchArchiveUrl is not null;
            bool hasInstaller = updateResult.HasInstaller;

            if (!hasManifest || !hasArchive)
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: hasInstaller,
                    statusCode: "missing-patch-assets",
                    message: "Patch assets are incomplete for this release.",
                    plan: null,
                    manifest: null,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            PatchManifest? manifest = await GetManifestAsync(updateResult.PatchManifestUrl!, cancellationToken);
            if (manifest is null)
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: hasInstaller,
                    statusCode: "manifest-unavailable",
                    message: "Patch manifest could not be downloaded.",
                    plan: null,
                    manifest: null,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            if (!TryValidateAllowedBaseVersions(manifest, currentVersion, out _, out string? matchedBaseVersion, out string? baseVersionStatusCode, out string? baseVersionMessage))
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: baseVersionStatusCode,
                    message: baseVersionMessage,
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            if (!VersionsMatch(manifest.TargetVersion, updateResult.TagName))
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: "target-version-mismatch",
                    message: "Patch manifest target version does not match the selected release.",
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            if (manifest.Files is null || manifest.Files.Count == 0)
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: "manifest-empty",
                    message: "Patch manifest does not contain any file entries.",
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            string archiveName = string.IsNullOrWhiteSpace(updateResult.PatchArchiveName)
                ? Path.GetFileName(updateResult.PatchArchiveUrl!.AbsolutePath)
                : updateResult.PatchArchiveName;
            var plan = new PatchPlan(manifest, updateResult.PatchArchiveUrl!, archiveName);

            return new PatchPreflightResult(
                eligible: true,
                requiresInstaller: false,
                statusCode: "eligible",
                message: $"Patch chain is compatible with base {matchedBaseVersion}.",
                plan: plan,
                manifest: manifest,
                hasManifest: hasManifest,
                hasArchive: hasArchive,
                hasInstaller: hasInstaller);
        }

        public static async Task<string?> DownloadPatchArchiveAsync(
            PatchPlan plan,
            Action<long, long?, double?>? progress,
            CancellationToken cancellationToken)
        {
            string stagingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VaultSync",
                "patches");
            Directory.CreateDirectory(stagingDir);

            string destinationPath = Path.Combine(stagingDir, plan.ArchiveName);

            // If the file already exists and matches size/hash, reuse it instead of re-downloading.
            if (File.Exists(destinationPath))
            {
                var existing = new FileInfo(destinationPath);
                bool sizeOk   = plan.Manifest.ArchiveSize <= 0 || existing.Length == plan.Manifest.ArchiveSize;
                bool hashOk   = await VerifyChecksumAsync(destinationPath, plan.Manifest.ArchiveSha256, cancellationToken);
                if (sizeOk && hashOk)
                {
                    return destinationPath;
                }
            }

            using (HttpResponseMessage response = await s_httpClient.GetAsync(plan.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                long? totalBytes = response.Content.Headers.ContentLength;
                await using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream destinationStream = File.Create(destinationPath);
                await CopyToWithProgressAsync(sourceStream, destinationStream, totalBytes, progress, cancellationToken);
            }

            var downloaded = new FileInfo(destinationPath);
            if (plan.Manifest.ArchiveSize > 0 && downloaded.Length != plan.Manifest.ArchiveSize)
                return null;

            if (!await VerifyChecksumAsync(destinationPath, plan.Manifest.ArchiveSha256, cancellationToken))
                return null;

            return destinationPath;
        }

        private static async Task CopyToWithProgressAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            Action<long, long?, double?>? progress,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 128];
            long totalRead = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan lastReport = TimeSpan.Zero;
            long lastBytes = 0;

            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (progress is null)
                    continue;

                TimeSpan elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport < TimeSpan.FromMilliseconds(250))
                    continue;

                long deltaBytes = totalRead - lastBytes;
                double deltaTime = (elapsed - lastReport).TotalSeconds;
                double? bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;

                progress(totalRead, totalBytes, bytesPerSecond);
                lastReport = elapsed;
                lastBytes = totalRead;
            }

            if (progress is not null)
            {
                TimeSpan elapsed = stopwatch.Elapsed;
                long deltaBytes = totalRead - lastBytes;
                double deltaTime = (elapsed - lastReport).TotalSeconds;
                double? bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;
                progress(totalRead, totalBytes, bytesPerSecond);
            }
        }

        private static async Task<bool> VerifyChecksumAsync(
            string filePath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
                return true;

            using FileStream stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
            string actual = HashService.FormatSha256Lower(hash);
            return string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool VersionsMatch(string? previousVersion, string? currentVersion)
        {
            string normalizedPrevious = VersionHelper.NormalizeIdentity(previousVersion);
            string normalizedCurrent = VersionHelper.NormalizeIdentity(currentVersion);
            Version? manifestVersion = VersionHelper.TryParse(previousVersion);
            Version? currentParsed = VersionHelper.TryParse(currentVersion);

            if (manifestVersion is not null && currentParsed is not null)
            {
                bool sameCore = manifestVersion.Major == currentParsed.Major
                               && manifestVersion.Minor == currentParsed.Minor
                               && manifestVersion.Build == currentParsed.Build;

                if (!sameCore)
                    return false;

                int revA = manifestVersion.Revision;
                int revB = currentParsed.Revision;

                // Treat missing revision (-1) as 0, but do not ignore non-zero revisions.
                if (revA == revB || (revA == -1 && revB == 0) || (revB == -1 && revA == 0))
                {
                    string prereleaseA = VersionHelper.GetPrereleaseLabel(previousVersion);
                    string prereleaseB = VersionHelper.GetPrereleaseLabel(currentVersion);
                    return string.Equals(prereleaseA, prereleaseB, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }

            return string.Equals(
                normalizedPrevious,
                normalizedCurrent,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetAllowedBaseVersions(
            PatchManifest manifest,
            out IReadOnlyList<string> allowedBaseVersions,
            out string statusCode,
            out string message)
        {
            allowedBaseVersions = [];
            statusCode = string.Empty;
            message = string.Empty;

            if (manifest is null)
            {
                statusCode = InvalidBaseAllowlistStatus;
                message = "Patch manifest is missing.";
                return false;
            }

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in manifest.BaseVersions ?? [])
            {
                string normalizedVersion = VersionHelper.NormalizeIdentity(raw);
                if (string.IsNullOrWhiteSpace(normalizedVersion))
                {
                    statusCode = InvalidBaseAllowlistStatus;
                    message = "Patch manifest contains an empty allowed base version entry.";
                    return false;
                }

                if (!seen.Add(normalizedVersion))
                    continue;

                normalized.Add(normalizedVersion);
            }

            string legacyBase = VersionHelper.NormalizeIdentity(manifest.PreviousVersion);
            if (!string.IsNullOrWhiteSpace(legacyBase))
            {
                if (normalized.Count == 0)
                {
                    normalized.Add(legacyBase);
                }
                else if (!normalized.Contains(legacyBase, StringComparer.OrdinalIgnoreCase))
                {
                    statusCode = InvalidBaseAllowlistStatus;
                    message = "Patch manifest previousVersion is not included in the allowed base version list.";
                    return false;
                }
            }

            if (normalized.Count == 0)
            {
                statusCode = InvalidBaseAllowlistStatus;
                message = "Patch manifest does not declare any allowed base versions.";
                return false;
            }

            allowedBaseVersions = normalized
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        internal static bool TryValidateAllowedBaseVersions(
            PatchManifest manifest,
            string currentVersion,
            out IReadOnlyList<string> allowedBaseVersions,
            out string matchedBaseVersion,
            out string statusCode,
            out string message)
        {
            matchedBaseVersion = string.Empty;

            if (!TryGetAllowedBaseVersions(manifest, out allowedBaseVersions, out statusCode, out message))
                return false;

            string normalizedCurrent = VersionHelper.NormalizeIdentity(currentVersion);
            if (string.IsNullOrWhiteSpace(normalizedCurrent))
            {
                statusCode = "base-version-not-allowed";
                message = "Current installed version is empty or invalid for patch matching.";
                return false;
            }

            matchedBaseVersion = allowedBaseVersions
                .FirstOrDefault(value => VersionsMatch(value, normalizedCurrent)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(matchedBaseVersion))
            {
                statusCode = "base-version-not-allowed";
                message = $"Patch manifest allows [{string.Join(", ", allowedBaseVersions)}], but current version is {normalizedCurrent}.";
                return false;
            }

            statusCode = "eligible";
            message = string.Empty;
            return true;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-PatchUpdater/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string? token = Environment.GetEnvironmentVariable("VAULTSYNC_GH_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }

        private static async Task<PatchManifest?> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken)
        {
            if (s_manifestCache.TryGetValue(manifestUrl, out (PatchManifest Manifest, DateTimeOffset FetchedAt) cached))
            {
                if (DateTimeOffset.UtcNow - cached.FetchedAt < s_manifestCacheWindow)
                {
                    return cached.Manifest;
                }
            }

            PatchManifest? manifest = await s_httpClient.GetFromJsonAsync<PatchManifest>(manifestUrl, cancellationToken);
            if (manifest is null)
                return null;

            s_manifestCache[manifestUrl] = (manifest, DateTimeOffset.UtcNow);
            return manifest;
        }

    }
}
