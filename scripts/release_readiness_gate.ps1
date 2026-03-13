param(
    [string]$TargetVersion,
    [string]$ReleaseTrack,
    [string]$Repository = "ATAC-Helicopter/VaultSync",
    [int]$ProjectNumber = 7,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-Result {
    param(
        [string]$Code,
        [bool]$Passed,
        [string]$Message,
        [hashtable]$Data = @{}
    )

    [pscustomobject]@{
        code    = $Code
        passed  = $Passed
        message = $Message
        data    = [pscustomobject]$Data
    }
}

function Get-FileVersionValue {
    param(
        [string]$Path,
        [string]$Pattern
    )

    $content = Get-Content $Path -Raw
    $match = [regex]::Match($content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "Could not find version pattern '$Pattern' in '$Path'."
    }

    return $match.Groups[1].Value.Trim()
}

function Get-ChangelogVersion {
    $line = Get-Content CHANGELOG.md | Select-Object -First 3 | Where-Object { $_ -match '^## \[(.+?)\] - Unreleased' } | Select-Object -First 1
    if (-not $line) {
        throw "Could not find unreleased changelog header in CHANGELOG.md."
    }

    $match = [regex]::Match($line, '^## \[(.+?)\] - Unreleased')
    return $match.Groups[1].Value.Trim()
}

function Get-WhatsNewVersion {
    $line = Get-Content docs/WHATS_NEW.md | Where-Object { $_ -match '^## \[(.+?)\]' } | Select-Object -First 1
    if (-not $line) {
        throw "Could not find top version header in docs/WHATS_NEW.md."
    }

    $match = [regex]::Match($line, '^## \[(.+?)\]')
    return $match.Groups[1].Value.Trim()
}

function Get-GhJson {
    param(
        [string]$Command
    )

    $raw = Invoke-Expression $Command
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI command failed: $Command"
    }

    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json
}

function Get-ReleaseTrackFromVersion {
    param([string]$Version)

    $parts = $Version.Split(".")
    if ($parts.Length -lt 2) {
        throw "Version '$Version' is not in expected major.minor.patch format."
    }

    return "$($parts[0]).$($parts[1]).x"
}

if ([string]::IsNullOrWhiteSpace($TargetVersion)) {
    $TargetVersion = Get-FileVersionValue -Path "src/VaultSync.UI/VaultSync.UI.csproj" -Pattern '<Version>([^<]+)</Version>'
}

if ([string]::IsNullOrWhiteSpace($ReleaseTrack)) {
    $ReleaseTrack = Get-ReleaseTrackFromVersion -Version $TargetVersion
}

$results = New-Object System.Collections.Generic.List[object]

$uiVersion = Get-FileVersionValue -Path "src/VaultSync.UI/VaultSync.UI.csproj" -Pattern '<Version>([^<]+)</Version>'
$installerVersion = Get-FileVersionValue -Path "installer/VaultSyncInstaller.iss" -Pattern '#define MyAppVersion "([^"]+)"'
$changelogVersion = Get-ChangelogVersion
$whatsNewVersion = Get-WhatsNewVersion

$results.Add((New-Result -Code "version-ui" -Passed ($uiVersion -eq $TargetVersion) -Message "UI project version is '$uiVersion'." -Data @{ expected = $TargetVersion; actual = $uiVersion }))
$results.Add((New-Result -Code "version-installer" -Passed ($installerVersion -eq $TargetVersion) -Message "Installer version is '$installerVersion'." -Data @{ expected = $TargetVersion; actual = $installerVersion }))
$results.Add((New-Result -Code "docs-changelog" -Passed ($changelogVersion -eq $TargetVersion) -Message "Top unreleased changelog version is '$changelogVersion'." -Data @{ expected = $TargetVersion; actual = $changelogVersion }))
$results.Add((New-Result -Code "docs-whats-new" -Passed ($whatsNewVersion -eq $TargetVersion) -Message "Top What's New version is '$whatsNewVersion'." -Data @{ expected = $TargetVersion; actual = $whatsNewVersion }))
$results.Add((New-Result -Code "workflow-release-assets" -Passed (Test-Path ".github/workflows/release-assets.yml") -Message "Release assets workflow present." -Data @{ path = ".github/workflows/release-assets.yml" }))
$results.Add((New-Result -Code "script-build-patch" -Passed (Test-Path "scripts/build_patch.py") -Message "Patch build script present." -Data @{ path = "scripts/build_patch.py" }))

$releaseTag = "v$TargetVersion"
$release = $null
try {
    $release = Get-GhJson -Command "gh release view $releaseTag --repo $Repository --json tagName,isPrerelease,isDraft,assets,url"
} catch {
    $release = $null
}

if ($null -eq $release) {
    $results.Add((New-Result -Code "github-release" -Passed $false -Message "GitHub release '$releaseTag' not found." -Data @{ tag = $releaseTag; repository = $Repository }))
} else {
    $assetNames = @($release.assets | ForEach-Object { $_.name })
    $hasInstaller = $assetNames | Where-Object { $_ -like "VaultSync-Setup-*.exe" } | Select-Object -First 1
    $hasPatchManifest = $assetNames | Where-Object { $_ -like "vaultsync-patch-*.json" } | Select-Object -First 1
    $hasPatchArchive = $assetNames | Where-Object { $_ -like "vaultsync-patch-*.zip" } | Select-Object -First 1

    $results.Add((New-Result -Code "github-release" -Passed $true -Message "GitHub release '$releaseTag' found." -Data @{ url = $release.url; assetCount = $assetNames.Count }))
    $results.Add((New-Result -Code "asset-installer" -Passed ([bool]$hasInstaller) -Message "Installer asset presence check." -Data @{ expectedPattern = "VaultSync-Setup-*.exe"; assets = $assetNames }))
    $results.Add((New-Result -Code "asset-patch-manifest" -Passed ([bool]$hasPatchManifest) -Message "Patch manifest asset presence check." -Data @{ expectedPattern = "vaultsync-patch-*.json"; assets = $assetNames }))
    $results.Add((New-Result -Code "asset-patch-archive" -Passed ([bool]$hasPatchArchive) -Message "Patch archive asset presence check." -Data @{ expectedPattern = "vaultsync-patch-*.zip"; assets = $assetNames }))
}

$owner = ($Repository -split "/")[0]
$projectItems = Get-GhJson -Command "gh project item-list $ProjectNumber --owner $owner --limit 500 --format json"
$releaseItems = @($projectItems.items | Where-Object { $_.release -eq $ReleaseTrack })
$incompleteItems = @($releaseItems | Where-Object { $_.status -ne "Done" })

$results.Add((New-Result -Code "project-release-items" -Passed ($releaseItems.Count -gt 0) -Message "Project release slice '$ReleaseTrack' contains $($releaseItems.Count) item(s)." -Data @{ release = $ReleaseTrack; count = $releaseItems.Count }))
$results.Add((New-Result -Code "project-release-complete" -Passed ($incompleteItems.Count -eq 0) -Message "Project release slice '$ReleaseTrack' has $($incompleteItems.Count) incomplete item(s)." -Data @{
    release = $ReleaseTrack
    incomplete = @($incompleteItems | ForEach-Object {
        [pscustomobject]@{
            title  = $_.title
            number = $_.content.number
            status = $_.status
        }
    })
}))

$failed = @($results | Where-Object { -not $_.passed })
$summary = [pscustomobject]@{
    targetVersion = $TargetVersion
    releaseTrack  = $ReleaseTrack
    repository    = $Repository
    checkedUtc    = [DateTimeOffset]::UtcNow.ToString("O")
    passed        = ($failed.Count -eq 0)
    failedCount   = $failed.Count
    checks        = $results
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
} else {
    if ($summary.passed) {
        Write-Host "Release readiness gate: PASS" -ForegroundColor Green
    } else {
        Write-Host "Release readiness gate: FAIL ($($summary.failedCount) check(s))" -ForegroundColor Red
    }

    Write-Host "Target version : $TargetVersion"
    Write-Host "Release track  : $ReleaseTrack"
    Write-Host "Repository     : $Repository"
    Write-Host ""

    foreach ($check in $results) {
        $prefix = if ($check.passed) { "[PASS]" } else { "[FAIL]" }
        Write-Host "$prefix $($check.code): $($check.message)"
        if (-not $check.passed -and $check.data) {
            $detail = ($check.data | ConvertTo-Json -Depth 6 -Compress)
            Write-Host "       $detail"
        }
    }
}

if (-not $summary.passed) {
    exit 1
}
