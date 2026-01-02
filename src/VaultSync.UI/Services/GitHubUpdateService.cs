using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
            string? installerName)
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
    }

    public enum GitHubReleaseChannel
    {
        Stable,
        Beta
    }

    public sealed class GitHubUpdateService
    {
        private const string ReleasesEndpointBase = "repos/ATAC-Helicopter/VaultSync/releases";
        private const int ReleasesPerPage = 5;
        private const int MaxReleasePages = 3;
        private const string StableBranchName = "stable";
        private const string DevBranchName = "dev";

        private static readonly HttpClient s_httpClient = CreateHttpClient();

        public async Task<UpdateCheckResult?> CheckForUpdateAsync(
            string currentVersion,
            GitHubReleaseChannel channel,
            CancellationToken cancellationToken)
        {
            var releases = await FetchReleasesAsync(cancellationToken);
            if (releases == null || releases.Count == 0)
                return null;

            var candidate = channel switch
            {
                GitHubReleaseChannel.Beta => SelectBestBetaOrStableCandidate(releases),
                _                        => SelectStableCandidate(releases)
            };

            if (candidate == null)
                return null;

            var releaseTag = (candidate.TagName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(releaseTag))
                return null;

            if (!IsReleaseNewer(releaseTag, currentVersion))
                return null;

            if (!Uri.TryCreate(candidate.HtmlUrl, UriKind.Absolute, out var releaseUri))
            {
                releaseUri = new Uri("https://github.com/ATAC-Helicopter/VaultSync/releases");
            }

            var releaseName = string.IsNullOrWhiteSpace(candidate.Name) ? releaseTag : candidate.Name;
            var releaseNotes = candidate.Body ?? string.Empty;
            var publishedAt = candidate.PublishedAt ?? DateTime.MinValue;

            var (manifestUrl, archiveUrl, archiveName) = GetPatchAssets(candidate.Assets);
            var (installerUrl, installerName) = GetInstallerAsset(candidate.Assets);

            return new UpdateCheckResult(
                releaseTag,
                releaseName,
                releaseNotes,
                releaseUri,
                publishedAt,
                manifestUrl,
                archiveUrl,
                archiveName,
                installerUrl,
                installerName);
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

            var separatorIndex = trimmed.IndexOfAny(new[] { '-', '+' });
            return separatorIndex >= 0;
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

            for (var page = 1; page <= MaxReleasePages; page++)
            {
                var endpoint = $"{ReleasesEndpointBase}?per_page={ReleasesPerPage}&page={page}";
                List<GitHubRelease>? pageReleases;

                try
                {
                    pageReleases = await s_httpClient.GetFromJsonAsync<List<GitHubRelease>>(endpoint, cancellationToken);
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

            var platformSuffix = GetPlatformSuffix();
            if (string.IsNullOrEmpty(platformSuffix))
                return (null, null, null);

            var manifestName = $"vaultsync-patch-{platformSuffix}.json";
            var archiveName = $"vaultsync-patch-{platformSuffix}.zip";

            var manifest = assets.FirstOrDefault(a => string.Equals(a.Name, manifestName, StringComparison.OrdinalIgnoreCase));
            var archive = assets.FirstOrDefault(a => string.Equals(a.Name, archiveName, StringComparison.OrdinalIgnoreCase));

            if (manifest is null || archive is null || string.IsNullOrEmpty(manifest.BrowserDownloadUrl))
                return (null, null, null);

            Uri? archiveUri = null;
            if (!string.IsNullOrWhiteSpace(archive.BrowserDownloadUrl) &&
                Uri.TryCreate(archive.BrowserDownloadUrl, UriKind.Absolute, out var parsed))
            {
                archiveUri = parsed;
            }

            return (manifest.BrowserDownloadUrl, archiveUri, archive.Name);
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
                asset = assets.FirstOrDefault(a =>
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

        private static string GetPlatformSuffix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macos";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "linux";
            return string.Empty;
        }
    }
}
