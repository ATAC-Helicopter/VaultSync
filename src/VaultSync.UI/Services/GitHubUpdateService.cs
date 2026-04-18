using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;

namespace VaultSync.UI.Services
{
    public sealed class UpdateCheckResult
    {
        public UpdateCheckResult(
            string tagName,
            string releaseName,
            string releaseNotes,
            Uri releaseUrl,
            DateTime publishedAt,
            string? patchManifestUrl,
            Uri? patchArchiveUrl,
            string? patchArchiveName,
            Uri? installerUrl,
            string? installerName,
            UpdateCheckDiagnostics diagnostics)
        {
            TagName          = tagName;
            ReleaseName      = releaseName;
            ReleaseNotes     = releaseNotes;
            ReleaseUrl       = releaseUrl;
            PublishedAt      = publishedAt;
            PatchManifestUrl = patchManifestUrl;
            PatchArchiveUrl  = patchArchiveUrl;
            PatchArchiveName = patchArchiveName;
            InstallerUrl     = installerUrl;
            InstallerName    = installerName;
            Diagnostics      = diagnostics;
        }

        public string TagName { get; }
        public string ReleaseName { get; }
        public string ReleaseNotes { get; }
        public Uri ReleaseUrl { get; }
        public DateTime PublishedAt { get; }
        public string? PatchManifestUrl { get; }
        public Uri? PatchArchiveUrl { get; }
        public string? PatchArchiveName { get; }
        public Uri? InstallerUrl { get; }
        public string? InstallerName { get; }
        public bool HasPatch => !string.IsNullOrWhiteSpace(PatchManifestUrl) && PatchArchiveUrl != null;
        public bool HasInstaller => InstallerUrl != null;
        public UpdateCheckDiagnostics Diagnostics { get; }
    }

    public sealed class UpdateCheckEvaluation
    {
        public UpdateCheckEvaluation(UpdateCheckResult? update, UpdateCheckDiagnostics diagnostics)
        {
            Update = update;
            Diagnostics = diagnostics;
        }

        public UpdateCheckResult? Update { get; }
        public UpdateCheckDiagnostics Diagnostics { get; }
    }

    public enum GitHubReleaseChannel
    {
        Stable,
        Beta
    }

    public sealed class GitHubUpdateService
    {
        private const string ReleasesEndpointBase = "repos/ATAC-Helicopter/VaultSync/releases";
        private const int ReleasesPerPage = 10;
        private const int MaxReleasePages = 1;
        private const string StableBranchName = "stable";
        private const string DevBranchName = "dev";

        private static readonly HttpClient s_httpClient = CreateHttpClient();
        private static readonly object s_releaseCacheLock = new();
        private static string? s_releaseEtag;
        private static List<GitHubRelease>? s_releaseCache;
        private static DateTimeOffset? s_releaseCacheTimestamp;
        private static readonly TimeSpan s_releaseCacheTtl = TimeSpan.FromMinutes(5);

        public async Task<UpdateCheckEvaluation> CheckForUpdateAsync(
            string currentVersion,
            GitHubReleaseChannel channel,
            CancellationToken cancellationToken)
        {
            Console.WriteLine($"[Update] Fetching GitHub releases (channel={channel}, current={currentVersion}).");
            var diagnostics = new UpdateCheckDiagnostics
            {
                CheckedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Channel = channel.ToString(),
                CurrentVersion = currentVersion?.Trim() ?? string.Empty
            };
            var releases = await FetchReleasesAsync(cancellationToken).ConfigureAwait(false);
            if (releases == null || releases.Count == 0)
            {
                Console.WriteLine("[Update] No releases returned from GitHub.");
                diagnostics.Decision = "no-releases";
                return new UpdateCheckEvaluation(null, diagnostics);
            }

            var betaCandidate = SelectBetaCandidate(releases);
            var stableCandidate = SelectStableCandidate(releases);
            diagnostics.BetaCandidate = ToDiagnostics(betaCandidate);
            diagnostics.StableCandidate = ToDiagnostics(stableCandidate);

            if (channel == GitHubReleaseChannel.Beta)
            {
                Console.WriteLine($"[Update] Beta candidate: {DescribeRelease(betaCandidate)}");
                Console.WriteLine($"[Update] Stable candidate: {DescribeRelease(stableCandidate)}");
            }
            else
            {
                Console.WriteLine($"[Update] Stable candidate: {DescribeRelease(stableCandidate)}");
            }

            var candidate = channel == GitHubReleaseChannel.Beta
                ? SelectBestBetaOrStableCandidate(releases)
                : SelectStableCandidate(releases);
            diagnostics.SelectedCandidate = ToDiagnostics(candidate);

            if (candidate == null)
            {
                Console.WriteLine("[Update] No suitable release candidate found.");
                diagnostics.Decision = "no-suitable-candidate";
                return new UpdateCheckEvaluation(null, diagnostics);
            }

            var releaseTag = (candidate.TagName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(releaseTag))
            {
                diagnostics.Decision = "candidate-missing-tag";
                return new UpdateCheckEvaluation(null, diagnostics);
            }

            Console.WriteLine($"[Update] Candidate release: tag={releaseTag}, prerelease={candidate.Prerelease}, published={candidate.PublishedAt:O}, target={candidate.TargetCommitish}.");

            if (!IsReleaseNewer(releaseTag, currentVersion))
            {
                Console.WriteLine("[Update] Candidate is not newer than current version.");
                diagnostics.Decision = "candidate-not-newer";
                return new UpdateCheckEvaluation(null, diagnostics);
            }

            if (!Uri.TryCreate(candidate.HtmlUrl, UriKind.Absolute, out var releaseUri))
            {
                releaseUri = new Uri("https://github.com/ATAC-Helicopter/VaultSync/releases");
            }

            var releaseName = string.IsNullOrWhiteSpace(candidate.Name) ? releaseTag : candidate.Name;
            var releaseNotes = candidate.Body ?? string.Empty;
            var publishedAt = candidate.PublishedAt ?? DateTime.MinValue;

            var (manifestUrl, archiveUrl, archiveName) = GetPatchAssets(candidate.Assets);
            var (installerUrl, installerName) = GetInstallerAsset(candidate.Assets);
            diagnostics.SelectedCandidate = ToDiagnostics(candidate, !string.IsNullOrWhiteSpace(manifestUrl) && archiveUrl != null, installerUrl != null);
            diagnostics.Decision = channel == GitHubReleaseChannel.Beta
                ? "beta-or-stable-candidate-selected"
                : "stable-candidate-selected";

            return new UpdateCheckEvaluation(
                new UpdateCheckResult(
                releaseTag,
                releaseName,
                releaseNotes,
                releaseUri,
                publishedAt,
                manifestUrl,
                archiveUrl,
                archiveName,
                installerUrl,
                installerName,
                diagnostics),
                diagnostics);
        }

        private static bool IsReleaseNewer(string releaseTag, string currentVersion)
        {
            var releaseVersion = VersionHelper.TryParse(releaseTag);
            var localVersion = VersionHelper.TryParse(currentVersion);

            if (releaseVersion is not null && localVersion is not null)
            {
                if (releaseVersion > localVersion)
                    return true;
                if (releaseVersion < localVersion)
                    return false;

                var currentIsPrerelease = IsPrereleaseTag(currentVersion);
                var releaseIsPrerelease = IsPrereleaseTag(releaseTag);
                return currentIsPrerelease && !releaseIsPrerelease;
            }

            return !string.Equals(
                releaseTag,
                currentVersion?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrereleaseTag(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return false;

            var trimmed = version.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[1..];

            return trimmed.Contains('-', StringComparison.Ordinal);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri("https://api.github.com/"),
                Timeout     = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-Updater/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var token = Environment.GetEnvironmentVariable("VAULTSYNC_GH_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }

        private static async Task<List<GitHubRelease>> FetchReleasesAsync(CancellationToken cancellationToken)
        {
            var releases = new List<GitHubRelease>();
            var useCache = false;

            lock (s_releaseCacheLock)
            {
                if (s_releaseCache is { Count: > 0 } &&
                    s_releaseCacheTimestamp.HasValue &&
                    (DateTimeOffset.UtcNow - s_releaseCacheTimestamp.Value) <= s_releaseCacheTtl)
                {
                    return new List<GitHubRelease>(s_releaseCache);
                }
            }

            for (var page = 1; page <= MaxReleasePages; page++)
            {
                var endpoint = $"{ReleasesEndpointBase}?per_page={ReleasesPerPage}&page={page}";
                List<GitHubRelease>? pageReleases = null;
                HttpResponseMessage? response = null;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    string? cachedEtag;
                    lock (s_releaseCacheLock)
                    {
                        cachedEtag = s_releaseEtag;
                    }

                    if (!string.IsNullOrWhiteSpace(cachedEtag))
                    {
                        request.Headers.IfNoneMatch.ParseAdd(cachedEtag);
                    }

                    response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        useCache = true;
                        break;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    pageReleases = await response.Content
                        .ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (pageReleases is not { Count: > 0 })
                {
                    break;
                }

                releases.AddRange(pageReleases);

                var responseEtag = response?.Headers.ETag?.Tag;
                if (!string.IsNullOrWhiteSpace(responseEtag))
                {
                    lock (s_releaseCacheLock)
                    {
                        s_releaseEtag = responseEtag;
                        s_releaseCache = new List<GitHubRelease>(releases);
                        s_releaseCacheTimestamp = DateTimeOffset.UtcNow;
                    }
                }

            }

            if (useCache)
            {
                lock (s_releaseCacheLock)
                {
                    return s_releaseCache ?? new List<GitHubRelease>();
                }
            }

            if (releases.Count > 0)
            {
                lock (s_releaseCacheLock)
                {
                    s_releaseCache = new List<GitHubRelease>(releases);
                    s_releaseCacheTimestamp = DateTimeOffset.UtcNow;
                }
            }

            return releases;
        }

        private static GitHubRelease? SelectStableCandidate(List<GitHubRelease> releases)
        {
            var stableBranch = releases
                .Where(r => !r.Draft && !r.Prerelease && string.Equals(r.TargetCommitish, StableBranchName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.PublishedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            return stableBranch ?? releases.FirstOrDefault(r => !r.Draft && !r.Prerelease);
        }

        private static GitHubRelease? SelectBetaCandidate(List<GitHubRelease> releases)
        {
            var devBranch = releases
                .Where(r => !r.Draft && string.Equals(r.TargetCommitish, DevBranchName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.PublishedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            return devBranch ?? SelectStableCandidate(releases);
        }

        private static GitHubRelease? SelectBestBetaOrStableCandidate(List<GitHubRelease> releases)
        {
            var betaCandidate = SelectBetaCandidate(releases);
            var stableCandidate = SelectStableCandidate(releases);

            if (betaCandidate is null)
                return stableCandidate;
            if (stableCandidate is null)
                return betaCandidate;

            var betaVersion = VersionHelper.TryParse(betaCandidate.TagName);
            var stableVersion = VersionHelper.TryParse(stableCandidate.TagName);

            if (betaVersion is not null && stableVersion is not null)
            {
                if (betaVersion > stableVersion)
                    return betaCandidate;
                if (stableVersion > betaVersion)
                    return stableCandidate;
                return stableCandidate;
            }

            var betaDate = betaCandidate.PublishedAt ?? DateTime.MinValue;
            var stableDate = stableCandidate.PublishedAt ?? DateTime.MinValue;

            if (betaDate > stableDate)
                return betaCandidate;
            if (stableDate > betaDate)
                return stableCandidate;

            return stableCandidate;
        }

        private sealed class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string? TagName { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("body")]
            public string? Body { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }

            [JsonPropertyName("draft")]
            public bool Draft { get; set; }

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime? PublishedAt { get; set; }

            [JsonPropertyName("target_commitish")]
            public string? TargetCommitish { get; set; }

            [JsonPropertyName("assets")]
            public List<GitHubAsset>? Assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }
        }

        private static (string? ManifestUrl, Uri? ArchiveUrl, string? ArchiveName) GetPatchAssets(List<GitHubAsset>? assets)
        {
            if (assets is null || assets.Count == 0)
                return (null, null, null);

            var suffixes = GetPlatformSuffixes();
            if (suffixes.Count == 0)
                return (null, null, null);

            foreach (var platformSuffix in suffixes)
            {
                var manifestName = $"vaultsync-patch-{platformSuffix}.json";
                var archiveName = $"vaultsync-patch-{platformSuffix}.zip";

                var manifest = assets.FirstOrDefault(a => string.Equals(a.Name, manifestName, StringComparison.OrdinalIgnoreCase));
                var archive = assets.FirstOrDefault(a => string.Equals(a.Name, archiveName, StringComparison.OrdinalIgnoreCase));

                if (manifest is null || archive is null || string.IsNullOrEmpty(manifest.BrowserDownloadUrl))
                    continue;

                Uri? archiveUri = null;
                if (!string.IsNullOrWhiteSpace(archive.BrowserDownloadUrl) &&
                    Uri.TryCreate(archive.BrowserDownloadUrl, UriKind.Absolute, out var parsed))
                {
                    archiveUri = parsed;
                }

                return (manifest.BrowserDownloadUrl, archiveUri, archive.Name);
            }

            return (null, null, null);
        }

        private static (Uri? InstallerUrl, string? InstallerName) GetInstallerAsset(List<GitHubAsset>? assets)
        {
            if (assets is null || assets.Count == 0)
                return (null, null);

            GitHubAsset? asset = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                asset = assets.FirstOrDefault(a =>
                    a.Name is not null &&
                    a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                asset = assets.FirstOrDefault(a =>
                    a.Name is not null &&
                    a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                foreach (var suffix in GetLinuxAssetSuffixes())
                {
                    asset = assets.FirstOrDefault(a =>
                        a.Name is not null &&
                        a.Name.Contains(suffix, StringComparison.OrdinalIgnoreCase) &&
                        (a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
                         a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));
                    if (asset != null)
                        break;
                }

                asset ??= assets.FirstOrDefault(a =>
                    a.Name is not null &&
                    (a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
                     a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));
            }

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return (null, null);

            return Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url)
                ? (url, asset.Name)
                : (null, null);
        }

        private static List<string> GetPlatformSuffixes()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new List<string> { "windows" };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var suffix = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "macos-apple-silicon"
                    : "macos-intel";
                return new List<string> { suffix, "macos" };
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var suffixes = GetLinuxPatchSuffixes();
                suffixes.Add("linux");
                return suffixes;
            }
            return new List<string>();
        }

        private static List<string> GetLinuxPatchSuffixes()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => new List<string> { "linux-arm64" },
                _ => new List<string> { "linux-x64" }
            };
        }

        private static List<string> GetLinuxAssetSuffixes()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => new List<string> { "linux-arm64", "arm64", "aarch64" },
                _ => new List<string> { "linux-x64", "x64", "x86_64", "amd64" }
            };
        }

        private static string DescribeRelease(GitHubRelease? release)
        {
            if (release is null)
                return "none";

            var tag = release.TagName ?? "?";
            var target = string.IsNullOrWhiteSpace(release.TargetCommitish) ? "?" : release.TargetCommitish;
            var published = release.PublishedAt?.ToString("O") ?? "?";
            return $"tag={tag}, prerelease={release.Prerelease}, published={published}, target={target}";
        }

        private static UpdateReleaseCandidateDiagnostics ToDiagnostics(GitHubRelease? release, bool? hasPatch = null, bool? hasInstaller = null)
        {
            if (release is null)
                return new UpdateReleaseCandidateDiagnostics();

            var (manifestUrl, archiveUrl, _) = GetPatchAssets(release.Assets);
            var (installerUrl, _) = GetInstallerAsset(release.Assets);
            return new UpdateReleaseCandidateDiagnostics
            {
                Tag = (release.TagName ?? string.Empty).Trim(),
                TargetCommitish = (release.TargetCommitish ?? string.Empty).Trim(),
                Prerelease = release.Prerelease,
                PublishedUtc = release.PublishedAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                HasPatch = hasPatch ?? (!string.IsNullOrWhiteSpace(manifestUrl) && archiveUrl != null),
                HasInstaller = hasInstaller ?? installerUrl != null
            };
        }
    }
}
