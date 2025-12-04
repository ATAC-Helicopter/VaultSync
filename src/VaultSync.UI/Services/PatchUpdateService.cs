using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

        [JsonPropertyName("targetVersion")]
        public string TargetVersion { get; set; } = string.Empty;

        [JsonPropertyName("archiveSha256")]
        public string ArchiveSha256 { get; set; } = string.Empty;

        [JsonPropertyName("archiveSize")]
        public long ArchiveSize { get; set; }

        [JsonPropertyName("files")]
        public List<PatchFileEntry> Files { get; set; } = new();
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

    public sealed class PatchUpdateService
    {
        private static readonly HttpClient s_httpClient = CreateHttpClient();

        public async Task<PatchPlan?> PreparePatchAsync(
            UpdateCheckResult updateResult,
            string currentVersion,
            CancellationToken cancellationToken)
        {
            if (!updateResult.HasPatch || string.IsNullOrWhiteSpace(updateResult.PatchManifestUrl) || updateResult.PatchArchiveUrl is null)
                return null;

            var manifest = await s_httpClient.GetFromJsonAsync<PatchManifest>(
                updateResult.PatchManifestUrl,
                cancellationToken);

            if (manifest is null)
                return null;

            if (!VersionsMatch(manifest.PreviousVersion, currentVersion))
                return null;

            var archiveName = string.IsNullOrWhiteSpace(updateResult.PatchArchiveName)
                ? Path.GetFileName(updateResult.PatchArchiveUrl.AbsolutePath)
                : updateResult.PatchArchiveName;

            return new PatchPlan(manifest, updateResult.PatchArchiveUrl, archiveName);
        }

        public async Task<string?> DownloadPatchArchiveAsync(
            PatchPlan plan,
            CancellationToken cancellationToken)
        {
            var stagingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VaultSync",
                "patches");
            Directory.CreateDirectory(stagingDir);

            var destinationPath = Path.Combine(stagingDir, plan.ArchiveName);

            // If the file already exists and matches size/hash, reuse it instead of re-downloading.
            if (File.Exists(destinationPath))
            {
                var existing = new FileInfo(destinationPath);
                var sizeOk   = plan.Manifest.ArchiveSize <= 0 || existing.Length == plan.Manifest.ArchiveSize;
                var hashOk   = await VerifyChecksumAsync(destinationPath, plan.Manifest.ArchiveSha256, cancellationToken);
                if (sizeOk && hashOk)
                {
                    return destinationPath;
                }
            }

            using (var response = await s_httpClient.GetAsync(plan.ArchiveUrl, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var destinationStream = File.Create(destinationPath);
                await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            }

            var downloaded = new FileInfo(destinationPath);
            if (plan.Manifest.ArchiveSize > 0 && downloaded.Length != plan.Manifest.ArchiveSize)
                return null;

            if (!await VerifyChecksumAsync(destinationPath, plan.Manifest.ArchiveSha256, cancellationToken))
                return null;

            return destinationPath;
        }

        private static async Task<bool> VerifyChecksumAsync(
            string filePath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
                return true;

            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken);
            var actual = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            return string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool VersionsMatch(string? previousVersion, string? currentVersion)
        {
            var manifestVersion = VersionHelper.TryParse(previousVersion);
            var currentParsed = VersionHelper.TryParse(currentVersion);

            if (manifestVersion is not null && currentParsed is not null)
            {
                var sameCore = manifestVersion.Major == currentParsed.Major
                               && manifestVersion.Minor == currentParsed.Minor
                               && manifestVersion.Build == currentParsed.Build;

                if (!sameCore)
                    return false;

                var revA = manifestVersion.Revision;
                var revB = currentParsed.Revision;

                // Treat missing revision (-1) as 0, but do not ignore non-zero revisions.
                if (revA == revB)
                    return true;

                if ((revA == -1 && revB == 0) || (revB == -1 && revA == 0))
                    return true;

                return false;
            }

            return string.Equals(
                previousVersion?.Trim(),
                currentVersion?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-PatchUpdater/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var token = Environment.GetEnvironmentVariable("VAULTSYNC_GH_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }
    }
}
