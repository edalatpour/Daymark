param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [string]$TraceId,
    [string]$OutputPath,
    [switch]$ListTraceIds
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file not found: $LogPath"
}

$allLines = Get-Content -LiteralPath $LogPath
$pattern = [regex]'TaskSaveTiming\[(?<trace>[^\]]+)\]\s+(?<step>[^\s]+)(?:\s+(?<ms>\d+)ms)?(?:\s+(?<details>.*))?'

$events = New-Object System.Collections.Generic.List[object]
$traceSeenOrder = New-Object System.Collections.Generic.List[string]
$traceSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)

for ($i = 0; $i -lt $allLines.Count; $i++) {
    $line = $allLines[$i]
    $match = $pattern.Match($line)
    if (-not $match.Success) {
        continue
    }

    $trace = $match.Groups['trace'].Value
    $step = $match.Groups['step'].Value
    $msRaw = $match.Groups['ms'].Value
    $details = $match.Groups['details'].Value.Trim()

    $ms = $null
    if (-not [string]::IsNullOrWhiteSpace($msRaw)) {
        $parsed = 0
        if ([int]::TryParse($msRaw, [ref]$parsed)) {
            $ms = $parsed
        }
    }

    if ($traceSet.Add($trace)) {
        $traceSeenOrder.Add($trace) | Out-Null
    }

    $events.Add([pscustomobject]@{
            Index   = $i
            Trace   = $trace
            Step    = $step
            Ms      = $ms
            Details = $details
            RawLine = $line
        }) | Out-Null
}

if ($events.Count -eq 0) {
    throw "No TaskSaveTiming events found in $LogPath"
}

if ($ListTraceIds) {
    $traceSeenOrder | ForEach-Object { Write-Output $_ }
    return
}

if ([string]::IsNullOrWhiteSpace($TraceId)) {
    $TraceId = $traceSeenOrder[$traceSeenOrder.Count - 1]
}

$traceEvents = $events | Where-Object { $_.Trace -eq $TraceId } | Sort-Object Index
if ($traceEvents.Count -eq 0) {
    throw "No TaskSaveTiming events found for trace '$TraceId'. Use -ListTraceIds to inspect available IDs."
}

function Format-CallLabel {
    param(
        [string]$Label,
        [Nullable[int]]$Ms,
        [string]$Details
    )

    $parts = @($Label)
    if ($Ms.HasValue) {
        $parts += "($Ms ms)"
    }
    if (-not [string]::IsNullOrWhiteSpace($Details)) {
        $parts += "[$Details]"
    }
    return ($parts -join " ")
}

function FormatMermaidText {
    param([string]$Text)
    if ($null -eq $Text) {
        return ""
    }
    return $Text.Replace('"', "'")
}

$mermaid = New-Object System.Collections.Generic.List[string]
$mermaid.Add("sequenceDiagram") | Out-Null
$mermaid.Add("autonumber") | Out-Null
$mermaid.Add("participant UI as TaskDetailsPage") | Out-Null
$mermaid.Add("participant VM as DailyViewModel") | Out-Null
$mermaid.Add("participant Repo as PlannerRepository") | Out-Null
$mermaid.Add("Note over UI,Repo: TaskSaveTiming trace $TraceId") | Out-Null

foreach ($timingRecord in $traceEvents) {
    $step = $timingRecord.Step
    $details = FormatMermaidText $timingRecord.Details
    $label = FormatMermaidText (Format-CallLabel -Label $step -Ms $timingRecord.Ms -Details $details)

    switch -Regex ($step) {
        '^page\.preclose\.begin$' {
            $mermaid.Add("UI->>UI: $label") | Out-Null
            continue
        }
        '^page\.preclose\.validation$' {
            $mermaid.Add("UI->>UI: $label") | Out-Null
            continue
        }
        '^page\.preclose\.suppress-sync$' {
            $mermaid.Add("UI->>VM: SuppressSyncForLocalSave $label") | Out-Null
            continue
        }
        '^page\.preclose\.local-save-attempt\.start$' {
            $mermaid.Add("UI->>VM: SaveTaskDetailsLocallyAsync $label") | Out-Null
            continue
        }
        '^page\.preclose\.local-save-attempt\.success$' {
            $mermaid.Add("VM-->>UI: SaveTaskDetailsLocallyAsync success $label") | Out-Null
            continue
        }
        '^page\.preclose\.local-save-attempt\.failure$' {
            $mermaid.Add("VM-->>UI: SaveTaskDetailsLocallyAsync failure $label") | Out-Null
            continue
        }
        '^page\.preclose\.local-save-complete$' {
            $mermaid.Add("UI->>UI: $label") | Out-Null
            continue
        }
        '^page\.preclose\.modal-close$' {
            $mermaid.Add("UI->>UI: $label") | Out-Null
            continue
        }
        '^page\.preclose\.total$' {
            $mermaid.Add("UI->>UI: $label") | Out-Null
            continue
        }
        '^vm\.preclose\.begin$' {
            $mermaid.Add("VM->>VM: $label") | Out-Null
            continue
        }
        '^vm\.preclose\.add-task$' {
            $mermaid.Add("VM->>Repo: AddTaskAsync $label") | Out-Null
            $mermaid.Add("Repo-->>VM: AddTaskAsync done") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement$' {
            $mermaid.Add("VM->>Repo: UpdateTaskOrder/UpdateTasks $label") | Out-Null
            $mermaid.Add("Repo-->>VM: UpdateTaskOrder done") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.place-in-memory$' {
            $mermaid.Add("VM->>VM: PlaceTaskInMemory $label") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.persist-order$' {
            $mermaid.Add("VM->>VM: Persist reordered tasks $label") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.sort-in-memory$' {
            $mermaid.Add("VM->>VM: SortTasksInMemory $label") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.total$' {
            $mermaid.Add("VM->>VM: ApplyTaskPlacementAsync total $label") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.update-order\.calculate$' {
            $mermaid.Add("VM->>VM: UpdateTaskOrderAsync calculate $label") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.update-order\.repo-update$' {
            $mermaid.Add("VM->>Repo: UpdateTasksAsync $label") | Out-Null
            $mermaid.Add("Repo-->>VM: UpdateTasksAsync done") | Out-Null
            continue
        }
        '^vm\.preclose\.apply-placement\.update-order\.total$' {
            $mermaid.Add("VM->>VM: UpdateTaskOrderAsync total $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.cleanup-children$' {
            $mermaid.Add("Repo->>Repo: DeleteChildTasksInternalAsync loop $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.lock-wait$' {
            $mermaid.Add("Repo->>Repo: Wait for _dbContextLock $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.sqlite-write-gate-wait$' {
            $mermaid.Add("Repo->>Repo: Wait for shared SQLite write gate $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.save-changes$' {
            $mermaid.Add("Repo->>Repo: SaveChangesAsync $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.save-changes\.prepare$' {
            $mermaid.Add("Repo->>Repo: UpdateRange/prepare save $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.save-changes\.db-call$' {
            $mermaid.Add("Repo->>Repo: SaveChangesAsync DB call $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.save-changes\.accept-all$' {
            $mermaid.Add("Repo->>Repo: ChangeTracker.AcceptAllChanges $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.commit$' {
            $mermaid.Add("Repo->>Repo: Commit transaction $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.total$' {
            $mermaid.Add("Repo->>Repo: UpdateTasksAsync total $label") | Out-Null
            continue
        }
        '^repo\.update-tasks\.skipped$' {
            $mermaid.Add("Repo-->>VM: UpdateTasksAsync skipped $label") | Out-Null
            continue
        }
        '^vm\.preclose\.update-task$' {
            $mermaid.Add("VM->>Repo: UpdateTaskAsync $label") | Out-Null
            $mermaid.Add("Repo-->>VM: UpdateTaskAsync done") | Out-Null
            continue
        }
        '^vm\.preclose\.forward$' {
            $mermaid.Add("VM->>Repo: ForwardTaskAsync $label") | Out-Null
            $mermaid.Add("Repo-->>VM: ForwardTaskAsync done") | Out-Null
            continue
        }
        '^vm\.preclose\.total$' {
            $mermaid.Add("VM-->>UI: SaveTaskDetailsLocallyAsync complete $label") | Out-Null
            continue
        }
        '^vm\.preclose\.skipped$' {
            $mermaid.Add("VM-->>UI: SaveTaskDetailsLocallyAsync skipped $label") | Out-Null
            continue
        }
        default {
            $mermaid.Add("Note over UI,Repo: $label") | Out-Null
            continue
        }
    }
}

$result = $mermaid -join [Environment]::NewLine

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Output $result
    return
}

Set-Content -LiteralPath $OutputPath -Value $result -Encoding utf8
Write-Output "Wrote Mermaid diagram to: $OutputPath"
