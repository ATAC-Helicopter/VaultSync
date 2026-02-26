param(
    [string]$Owner = 'ATAC-Helicopter',
    [int]$ProjectNumber = 1,
    [string]$RoadmapPath = 'ROADMAP.md'
)

$ErrorActionPreference = 'Stop'

function Normalize-Title([string]$title) {
    if ([string]::IsNullOrWhiteSpace($title)) { return '' }
    $t = ($title -replace '\s+', ' ').Trim()
    if ($t.EndsWith('.')) { $t = $t.Substring(0, $t.Length - 1) }
    return $t
}

function Build-RoadmapIndex([string]$path) {
    $index = @{}
    $lines = Get-Content $path
    $currentSection = ''
    $currentTitle = $null
    $buffer = New-Object System.Collections.Generic.List[string]
    $lastHeader = ''

    function Flush-Current {
        param($titleRef, $bufferRef, $sectionRef)
        if ([string]::IsNullOrWhiteSpace($titleRef)) { return }
        $k = Normalize-Title $titleRef
        $desc = ($bufferRef | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
        if ([string]::IsNullOrWhiteSpace($desc)) { return }
        $index[$k] = @{
            section = $sectionRef
            description = $desc.Trim()
        }
    }

    foreach ($line in $lines) {
        if ($line -match '^\s*#+\s+(.+?)\s*$') {
            $headerText = $matches[1].Trim()
            if ($headerText -ne $lastHeader) {
                $currentSection = $headerText
                $lastHeader = $headerText
            }
            continue
        }

        if ($line -match '^\s*-\s+\[[xX\ ]\]\s+`?(VS|ISS|BUG|REL)-\d+`?\s*[:\-]?\s*(.+?)\s*$') {
            Flush-Current -titleRef $currentTitle -bufferRef $buffer -sectionRef $currentSection
            $buffer.Clear()
            $rest = $matches[2].Trim()
            # Optional priority marker in roadmap ticket title, e.g. `P1` or P1
            $rest = ($rest -replace '^(?:`?P[0-2]`?\s+)', '').Trim()
            $ticketId = ([regex]::Match($line, '(VS|ISS|BUG|REL)-\d+')).Value
            $currentTitle = Normalize-Title "${ticketId}: $rest"
            continue
        }

        if ($line -match '^\s{2,}[-0-9A-Za-z`].*') {
            if (-not [string]::IsNullOrWhiteSpace($currentTitle)) {
                $buffer.Add(($line.Trim()))
            }
        }
    }

    Flush-Current -titleRef $currentTitle -bufferRef $buffer -sectionRef $currentSection
    return $index
}

$roadmapIndex = Build-RoadmapIndex -path $RoadmapPath
$project = gh project view $ProjectNumber --owner $Owner --format json | ConvertFrom-Json
$projectId = $project.id
$items = (gh project item-list $ProjectNumber --owner $Owner --limit 1000 --format json | ConvertFrom-Json).items

$updated = 0
$skipped = 0

foreach ($item in $items) {
    $title = Normalize-Title $item.title
    $matchKey = $null

    if ($roadmapIndex.ContainsKey($title)) {
        $matchKey = $title
    } else {
        $idMatch = [regex]::Match($title, '(VS|ISS|BUG|REL)-\d+')
        if ($idMatch.Success) {
            $ticketId = $idMatch.Value
            $candidate = $roadmapIndex.Keys | Where-Object { $_ -like "${ticketId}:*" } | Select-Object -First 1
            if ($candidate) { $matchKey = $candidate }
        }
    }

    if (-not $matchKey) {
        $skipped++
        continue
    }

    $info = $roadmapIndex[$matchKey]
    $status = if ([string]::IsNullOrWhiteSpace($item.status)) { 'Todo' } else { $item.status }
    $priority = if ([string]::IsNullOrWhiteSpace($item.priority)) { 'N/A' } else { $item.priority }
    $release = if ([string]::IsNullOrWhiteSpace($item.release)) { '1.9.x' } else { $item.release }
    $area = if ([string]::IsNullOrWhiteSpace($item.area)) { 'Core' } else { $item.area }

    $body = @(
        'Synced from ROADMAP.md'
        "Section: $($info.section)"
        "Status: $status"
        "Priority: $priority"
        "Release: $release"
        "Area: $area"
        ''
        'Description:'
        $info.description
    ) -join "`n"

    if ($item.content.type -eq 'DraftIssue') {
        gh project item-edit --id $item.content.id --project-id $projectId --title $title --body $body | Out-Null
        $updated++
        continue
    }

    if ($item.content.type -eq 'Issue') {
        $issueNumber = $item.content.number
        if ($issueNumber) {
            gh issue edit $issueNumber --repo "$Owner/VaultSync" --title $title --body $body | Out-Null
            $updated++
            continue
        }
    }

    $skipped++
}

Write-Host "Descriptions sync complete. updated=$updated skipped=$skipped indexed=$($roadmapIndex.Count)"
