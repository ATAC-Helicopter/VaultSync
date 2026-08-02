using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
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
        internal const long MaxPatchArchiveBytes = 4L * 1024 * 1024 * 1024;
        internal const long MaxExtractedPatchBytes = 8L * 1024 * 1024 * 1024;
        internal const int MaxPatchFileCount = 100_000;
        internal const long MaxPatchManifestBytes = 4L * 1024 * 1024;
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

            if (!updateResult.HasVerifiedPatch)
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: hasInstaller,
                    statusCode: "patch-asset-digest-missing",
                    message: "Patch assets are missing trusted GitHub digest metadata.",
                    plan: null,
                    manifest: null,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            PatchManifest? manifest;
            try
            {
                manifest = await GetManifestAsync(
                    updateResult.PatchManifestUrl!,
                    updateResult.PatchManifestSha256!,
                    updateResult.PatchManifestSize,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                manifest = null;
            }
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

            if (!TryValidatePatchManifest(manifest, out string? manifestStatusCode, out string? manifestMessage))
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: manifestStatusCode,
                    message: manifestMessage,
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            if (manifest.ArchiveSize != updateResult.PatchArchiveSize ||
                !string.Equals(
                    manifest.ArchiveSha256,
                    updateResult.PatchArchiveSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: "archive-release-digest-mismatch",
                    message: "Patch archive metadata does not match trusted GitHub release metadata.",
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

            string archiveName = string.IsNullOrWhiteSpace(updateResult.PatchArchiveName)
                ? Path.GetFileName(updateResult.PatchArchiveUrl!.AbsolutePath)
                : updateResult.PatchArchiveName;
            if (!TryGetSafeArchiveName(archiveName, out archiveName))
            {
                return new PatchPreflightResult(
                    eligible: false,
                    requiresInstaller: true,
                    statusCode: "archive-name-invalid",
                    message: "Patch archive name is not a safe ZIP file name.",
                    plan: null,
                    manifest: manifest,
                    hasManifest: hasManifest,
                    hasArchive: hasArchive,
                    hasInstaller: hasInstaller);
            }

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

            if (!TryGetSafeArchiveName(plan.ArchiveName, out string safeArchiveName) ||
                !TryValidatePatchManifest(plan.Manifest, out _, out _))
            {
                return null;
            }

            string destinationPath = Path.Combine(stagingDir, safeArchiveName);

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

            string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.download";
            try
            {
                using HttpResponseMessage response = await s_httpClient.GetAsync(
                    plan.ArchiveUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                long? totalBytes = response.Content.Headers.ContentLength;
                if (totalBytes is > MaxPatchArchiveBytes ||
                    (totalBytes.HasValue && totalBytes.Value != plan.Manifest.ArchiveSize))
                {
                    return null;
                }

                await using Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destinationStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await CopyToWithProgressAsync(
                        sourceStream,
                        destinationStream,
                        totalBytes,
                        plan.Manifest.ArchiveSize,
                        progress,
                        cancellationToken);
                }

                var downloaded = new FileInfo(temporaryPath);
                if (downloaded.Length != plan.Manifest.ArchiveSize)
                    return null;

                if (!await VerifyChecksumAsync(temporaryPath, plan.Manifest.ArchiveSha256, cancellationToken))
                    return null;

                File.Move(temporaryPath, destinationPath, overwrite: true);
                return destinationPath;
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup of an incomplete or rejected download.
                }
            }
        }

        private static async Task CopyToWithProgressAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            long maximumBytes,
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

                totalRead += read;
                if (totalRead > maximumBytes || totalRead > MaxPatchArchiveBytes)
                    throw new InvalidDataException("Patch archive exceeds its declared size.");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

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

        internal static bool TryValidatePatchManifest(
            PatchManifest manifest,
            out string statusCode,
            out string message)
        {
            statusCode = "manifest-invalid";
            message = string.Empty;
            if (manifest is null)
            {
                message = "Patch manifest is missing.";
                return false;
            }

            if (manifest.ArchiveSize <= 0 || manifest.ArchiveSize > MaxPatchArchiveBytes)
            {
                message = "Patch archive size is missing or outside the supported limit.";
                return false;
            }

            if (!IsSha256(manifest.ArchiveSha256))
            {
                message = "Patch archive SHA-256 is missing or invalid.";
                return false;
            }

            if (manifest.Files is null || manifest.Files.Count == 0)
            {
                message = "Patch manifest does not contain any file entries.";
                return false;
            }

            if (manifest.Files.Count > MaxPatchFileCount)
            {
                message = "Patch manifest contains too many file entries.";
                return false;
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedBytes = 0;
            foreach (PatchFileEntry file in manifest.Files)
            {
                string normalizedPath = file.RelativePath
                    .Replace('\\', '/')
                    .Normalize(NormalizationForm.FormC);
                if (!IsSafePatchRelativePath(normalizedPath) || !paths.Add(normalizedPath))
                {
                    message = $"Patch manifest contains an unsafe or duplicate file path: '{file.RelativePath}'.";
                    return false;
                }

                if (file.Size < 0 || !IsSha256(file.Sha256))
                {
                    message = $"Patch manifest contains invalid size or SHA-256 metadata for '{file.RelativePath}'.";
                    return false;
                }

                try
                {
                    extractedBytes = checked(extractedBytes + file.Size);
                }
                catch (OverflowException)
                {
                    message = "Patch manifest extracted size overflows the supported range.";
                    return false;
                }

                if (extractedBytes > MaxExtractedPatchBytes)
                {
                    message = "Patch manifest extracted size exceeds the supported limit.";
                    return false;
                }
            }

            statusCode = "eligible";
            return true;
        }

        internal static bool TryGetSafeArchiveName(string? archiveName, out string safeArchiveName)
        {
            safeArchiveName = (archiveName ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(safeArchiveName) &&
                   !safeArchiveName.Contains('/') &&
                   !safeArchiveName.Contains('\\') &&
                   !safeArchiveName.Contains(':') &&
                   string.Equals(Path.GetFileName(safeArchiveName), safeArchiveName, StringComparison.Ordinal) &&
                   safeArchiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   safeArchiveName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static bool IsSha256(string? value) =>
            value is { Length: 64 } && value.All(Uri.IsHexDigit);

        private static bool IsSafePatchRelativePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Replace('\\', '/').Normalize(NormalizationForm.FormC);
            return !normalized.StartsWith("/", StringComparison.Ordinal) &&
                   !normalized.Contains(':') &&
                   normalized.Split('/').All(IsSafePatchPathSegment);
        }

        private static bool IsSafePatchPathSegment(string part)
        {
            if (part is "" or "." or ".." ||
                part.EndsWith(' ') ||
                part.EndsWith('.') ||
                part.Any(char.IsControl))
            {
                return false;
            }

            string stem = part.Split('.')[0];
            return stem.ToUpperInvariant() is not
                ("CON" or "PRN" or "AUX" or "NUL" or
                 "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
                 "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
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

        private static async Task<PatchManifest?> GetManifestAsync(
            string manifestUrl,
            string expectedSha256,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            string cacheKey = $"{manifestUrl}|{expectedSha256}|{expectedSize}";
            if (s_manifestCache.TryGetValue(cacheKey, out (PatchManifest Manifest, DateTimeOffset FetchedAt) cached))
            {
                if (DateTimeOffset.UtcNow - cached.FetchedAt < s_manifestCacheWindow)
                {
                    return cached.Manifest;
                }
            }

            if (expectedSize <= 0 || expectedSize > MaxPatchManifestBytes || !IsSha256(expectedSha256))
                return null;

            using HttpResponseMessage response = await s_httpClient.GetAsync(
                manifestUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaxPatchManifestBytes ||
                (response.Content.Headers.ContentLength.HasValue &&
                 response.Content.Headers.ContentLength.Value != expectedSize))
            {
                return null;
            }

            byte[] payload = await ReadBoundedPayloadAsync(
                response.Content,
                expectedSize,
                cancellationToken);
            string actualSha256 = HashService.FormatSha256Lower(SHA256.HashData(payload));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256),
                    Convert.FromHexString(expectedSha256)))
            {
                return null;
            }

            PatchManifest? manifest = JsonSerializer.Deserialize<PatchManifest>(payload);
            if (manifest is null)
                return null;

            s_manifestCache[cacheKey] = (manifest, DateTimeOffset.UtcNow);
            return manifest;
        }

        private static async Task<byte[]> ReadBoundedPayloadAsync(
            HttpContent content,
            long expectedSize,
            CancellationToken cancellationToken)
        {
            await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
            using var destination = new MemoryStream((int)expectedSize);
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > expectedSize || total > MaxPatchManifestBytes)
                    throw new InvalidDataException("Patch manifest exceeds its trusted release size.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != expectedSize)
                throw new InvalidDataException("Patch manifest size does not match trusted release metadata.");
            return destination.ToArray();
        }

    }
}
