param(
    [string]$Owner = 'ATAC-Helicopter',
    [int]$ProjectNumber = 1,
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
