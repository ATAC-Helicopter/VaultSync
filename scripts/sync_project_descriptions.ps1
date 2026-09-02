param(
    [string]$Owner = 'ATAC-Helicopter',
    [int]$ProjectNumber = 7,
    [string]$RoadmapPath = 'ROADMAP.md',
    [switch]$DryRun,
    [string]$ItemsSnapshotPath = ''
)

$ErrorActionPreference = 'Stop'

$python = Get-Command python3 -ErrorAction SilentlyContinue
if (-not $python) {
    $python = Get-Command python -ErrorAction Stop
}

$scriptPath = Join-Path $PSScriptRoot 'roadmap_sync.py'
$arguments = @(
    $scriptPath,
    '--owner', $Owner,
    '--project-number', $ProjectNumber,
    '--roadmap-path', $RoadmapPath
)
if ($DryRun) {
    $arguments += '--dry-run'
}
if (-not [string]::IsNullOrWhiteSpace($ItemsSnapshotPath)) {
    $arguments += @('--items-snapshot-path', $ItemsSnapshotPath)
}

& $python.Source @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Roadmap description sync failed with exit code $LASTEXITCODE."
}

if ([string]::IsNullOrWhiteSpace($ItemsSnapshotPath)) {
    $dateGuardPath = Join-Path $PSScriptRoot 'project_date_guard.py'
    $dateArguments = @(
        $dateGuardPath,
        '--owner', $Owner,
        '--project-number', $ProjectNumber
    )
    if (-not $DryRun) {
        $dateArguments += '--apply'
    }

    & $python.Source @dateArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Project date integrity check failed with exit code $LASTEXITCODE."
    }
}
