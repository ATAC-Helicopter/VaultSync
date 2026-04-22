param(
    [string]$TargetVersion,
    [string]$ReleaseTrack,
    [string]$Repository = "ATAC-Helicopter/VaultSync",
    [int]$ProjectNumber = 7,
    [ValidateSet("PrePublish", "PostPublish")]
    [string]$Phase = "PrePublish",
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-Result {
    param(
        [string]$Code,
        [ValidateSet("pass", "warn", "fail")]
        [string]$Status,
        [string]$Message,
        [hashtable]$Data = @{}
    )

    [pscustomobject]@{
        code    = $Code
        status  = $Status
        passed  = ($Status -ne "fail")
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
    $line = Get-Content CHANGELOG.md | Select-Object -First 3 | Where-Object { $_ -match '^## \[(.+?)\] - (Unreleased|\d{2}\.\d{2}\.\d{4})' } | Select-Object -First 1
    if (-not $line) {
        throw "Could not find release changelog header in CHANGELOG.md."
    }

    $match = [regex]::Match($line, '^## \[(.+?)\] - (Unreleased|\d{2}\.\d{2}\.\d{4})')
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
    param([string]$Command)

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

function Add-CheckResult {
    param(
        [System.Collections.Generic.List[object]]$Results,
        [string]$Code,
        [bool]$Condition,
        [string]$PassMessage,
        [string]$FailMessage,
        [hashtable]$Data = @{},
        [switch]$WarningOnFail
    )

    $status = if ($Condition) {
        "pass"
    } elseif ($WarningOnFail) {
        "warn"
    } else {
        "fail"
    }

    $message = if ($Condition) { $PassMessage } else { $FailMessage }
    $Results.Add((New-Result -Code $Code -Status $status -Message $message -Data $Data))
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
$releasingDoc = Get-Content docs/RELEASING.md -Raw

Add-CheckResult -Results $results -Code "version-ui" -Condition ($uiVersion -eq $TargetVersion) `
    -PassMessage "UI project version is '$uiVersion'." `
    -FailMessage "UI project version '$uiVersion' does not match target '$TargetVersion'." `
    -Data @{ expected = $TargetVersion; actual = $uiVersion }

Add-CheckResult -Results $results -Code "version-installer" -Condition ($installerVersion -eq $TargetVersion) `
    -PassMessage "Installer version is '$installerVersion'." `
    -FailMessage "Installer version '$installerVersion' does not match target '$TargetVersion'." `
    -Data @{ expected = $TargetVersion; actual = $installerVersion }

Add-CheckResult -Results $results -Code "docs-changelog" -Condition ($changelogVersion -eq $TargetVersion) `
    -PassMessage "Top unreleased changelog version is '$changelogVersion'." `
    -FailMessage "Top unreleased changelog version '$changelogVersion' does not match target '$TargetVersion'." `
    -Data @{ expected = $TargetVersion; actual = $changelogVersion }

Add-CheckResult -Results $results -Code "docs-whats-new" -Condition ($whatsNewVersion -eq $TargetVersion) `
    -PassMessage "Top What's New version is '$whatsNewVersion'." `
    -FailMessage "Top What's New version '$whatsNewVersion' does not match target '$TargetVersion'." `
    -Data @{ expected = $TargetVersion; actual = $whatsNewVersion }

Add-CheckResult -Results $results -Code "workflow-release-assets" -Condition (Test-Path ".github/workflows/release-assets.yml") `
    -PassMessage "Release assets workflow present." `
    -FailMessage "Release assets workflow is missing." `
    -Data @{ path = ".github/workflows/release-assets.yml" }

Add-CheckResult -Results $results -Code "script-build-patch" -Condition (Test-Path "scripts/build_patch.py") `
    -PassMessage "Patch build script present." `
    -FailMessage "Patch build script is missing." `
    -Data @{ path = "scripts/build_patch.py" }

Add-CheckResult -Results $results -Code "script-release-gate" -Condition (Test-Path "scripts/release_readiness_gate.ps1") `
    -PassMessage "Release readiness gate script present." `
    -FailMessage "Release readiness gate script is missing." `
    -Data @{ path = "scripts/release_readiness_gate.ps1" }

Add-CheckResult -Results $results -Code "docs-release-checklist" -Condition ($releasingDoc -match 'release assets uploaded' -and $releasingDoc -match 'release_readiness_gate\.ps1') `
    -PassMessage "Release guide includes the release gate and asset-upload checklist." `
    -FailMessage "Release guide is missing release gate and/or asset-upload checklist coverage." `
    -Data @{ path = "docs/RELEASING.md" }

$releaseTag = "v$TargetVersion"
$release = $null
try {
    $release = Get-GhJson -Command "gh release view $releaseTag --repo $Repository --json tagName,isPrerelease,isDraft,assets,url"
} catch {
    $release = $null
}

$warnForPublishArtifacts = ($Phase -eq "PrePublish")
if ($null -eq $release) {
    Add-CheckResult -Results $results -Code "github-release" -Condition $false `
        -PassMessage "GitHub release '$releaseTag' found." `
        -FailMessage "GitHub release '$releaseTag' not found yet. Create the release before post-publish verification." `
        -Data @{ tag = $releaseTag; repository = $Repository; phase = $Phase } `
        -WarningOnFail:$warnForPublishArtifacts

    if ($warnForPublishArtifacts) {
        $results.Add((New-Result -Code "publish-assets-next-step" -Status "warn" -Message "Generate and upload release assets after creating the GitHub release." -Data @{
            nextSteps = @(
                "Run the release-assets GitHub Actions workflow for the target version.",
                "Upload installer and patch assets to the GitHub release.",
                "Rerun the gate with -Phase PostPublish."
            )
        }))
    }
} else {
    $assetNames = @($release.assets | ForEach-Object { $_.name })
    $hasInstaller = [bool]($assetNames | Where-Object { $_ -like "VaultSync-Setup-*.exe" } | Select-Object -First 1)
    $hasPatchManifest = [bool]($assetNames | Where-Object { $_ -like "vaultsync-patch-*.json" } | Select-Object -First 1)
    $hasPatchArchive = [bool]($assetNames | Where-Object { $_ -like "vaultsync-patch-*.zip" } | Select-Object -First 1)

    Add-CheckResult -Results $results -Code "github-release" -Condition $true `
        -PassMessage "GitHub release '$releaseTag' found." `
        -FailMessage "" `
        -Data @{ url = $release.url; assetCount = $assetNames.Count; phase = $Phase }

    Add-CheckResult -Results $results -Code "asset-installer" -Condition $hasInstaller `
        -PassMessage "Installer asset is present on the release." `
        -FailMessage "Installer asset is missing. Generate/upload installer assets before shipping." `
        -Data @{ expectedPattern = "VaultSync-Setup-*.exe"; assets = $assetNames } `
        -WarningOnFail:$warnForPublishArtifacts

    Add-CheckResult -Results $results -Code "asset-patch-manifest" -Condition $hasPatchManifest `
        -PassMessage "Patch manifest asset is present on the release." `
        -FailMessage "Patch manifest asset is missing. Generate/upload patch assets before shipping." `
        -Data @{ expectedPattern = "vaultsync-patch-*.json"; assets = $assetNames } `
        -WarningOnFail:$warnForPublishArtifacts

    Add-CheckResult -Results $results -Code "asset-patch-archive" -Condition $hasPatchArchive `
        -PassMessage "Patch archive asset is present on the release." `
        -FailMessage "Patch archive asset is missing. Generate/upload patch assets before shipping." `
        -Data @{ expectedPattern = "vaultsync-patch-*.zip"; assets = $assetNames } `
        -WarningOnFail:$warnForPublishArtifacts

    if ($warnForPublishArtifacts -and (-not ($hasInstaller -and $hasPatchManifest -and $hasPatchArchive))) {
        $results.Add((New-Result -Code "publish-assets-next-step" -Status "warn" -Message "Release exists but assets are incomplete. Run release asset generation before final verification." -Data @{
            missing = @(
                if (-not $hasInstaller) { "installer" }
                if (-not $hasPatchManifest) { "patch-manifest" }
                if (-not $hasPatchArchive) { "patch-archive" }
            )
            nextSteps = @(
                "Trigger the release-assets GitHub Actions workflow for the target version.",
                "Confirm generated assets are attached to the GitHub release.",
                "Rerun the gate with -Phase PostPublish."
            )
        }))
    }
}

$owner = ($Repository -split "/")[0]
$projectItems = Get-GhJson -Command "gh project item-list $ProjectNumber --owner $owner --limit 500 --format json"
$releaseItems = @($projectItems.items | Where-Object {
    $releaseProperty = $_.PSObject.Properties["release"]
    $itemRelease = if ($null -ne $releaseProperty) { $releaseProperty.Value } else { $null }
    $itemRelease -eq $ReleaseTrack
})
$incompleteItems = @($releaseItems | Where-Object {
    $statusProperty = $_.PSObject.Properties["status"]
    $itemStatus = if ($null -ne $statusProperty) { $statusProperty.Value } else { $null }
    $itemStatus -ne "Done"
})

Add-CheckResult -Results $results -Code "project-release-items" -Condition ($releaseItems.Count -gt 0) `
    -PassMessage "Project release slice '$ReleaseTrack' contains $($releaseItems.Count) item(s)." `
    -FailMessage "Project release slice '$ReleaseTrack' has no items." `
    -Data @{ release = $ReleaseTrack; count = $releaseItems.Count }

Add-CheckResult -Results $results -Code "project-release-complete" -Condition ($incompleteItems.Count -eq 0) `
    -PassMessage "Project release slice '$ReleaseTrack' is complete." `
    -FailMessage "Project release slice '$ReleaseTrack' still has incomplete work." `
    -Data @{
        release = $ReleaseTrack
        incomplete = @($incompleteItems | ForEach-Object {
            [pscustomobject]@{
                title  = $_.title
                number = $_.content.number
                status = $_.status
            }
        })
    }

$fails = @($results | Where-Object { $_.status -eq "fail" })
$warnings = @($results | Where-Object { $_.status -eq "warn" })
$summary = [pscustomobject]@{
    targetVersion = $TargetVersion
    releaseTrack  = $ReleaseTrack
    repository    = $Repository
    phase         = $Phase
    checkedUtc    = [DateTimeOffset]::UtcNow.ToString("O")
    passed        = ($fails.Count -eq 0)
    failedCount   = $fails.Count
    warningCount  = $warnings.Count
    checks        = $results
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
} else {
    if ($summary.passed) {
        $headline = if ($warnings.Count -gt 0) {
            "Release readiness gate: PASS with warnings ($($summary.warningCount))"
        } else {
            "Release readiness gate: PASS"
        }
        Write-Host $headline -ForegroundColor Green
    } else {
        Write-Host "Release readiness gate: FAIL ($($summary.failedCount) check(s), $($summary.warningCount) warning(s))" -ForegroundColor Red
    }

    Write-Host "Target version : $TargetVersion"
    Write-Host "Release track  : $ReleaseTrack"
    Write-Host "Phase          : $Phase"
    Write-Host "Repository     : $Repository"
    Write-Host ""

    foreach ($check in $results) {
        $prefix = switch ($check.status) {
            "pass" { "[PASS]" }
            "warn" { "[WARN]" }
            default { "[FAIL]" }
        }
        Write-Host "$prefix $($check.code): $($check.message)"
        if (($check.status -ne "pass") -and $check.data) {
            $detail = ($check.data | ConvertTo-Json -Depth 6 -Compress)
            Write-Host "       $detail"
        }
    }
}

if (-not $summary.passed) {
    exit 1
}
