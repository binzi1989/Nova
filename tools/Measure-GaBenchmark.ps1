param(
    [string]$CatalogPath = (Join-Path $PSScriptRoot "..\ga\benchmark-catalog.json"),
    [Parameter(Mandatory = $true)]
    [string]$ResultsPath,
    [string]$ReportPath = (Join-Path $PSScriptRoot "..\ga\benchmark-report.json")
)

$ErrorActionPreference = "Stop"
$catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$results = Get-Content -LiteralPath $ResultsPath -Raw -Encoding UTF8 | ConvertFrom-Json

if ($catalog.tasks.Count -ne 30) {
    throw "GA benchmark catalog must contain exactly 30 fixed tasks."
}

$expectedRuns = [int]$catalog.runs_per_task
$evaluations = foreach ($task in $catalog.tasks) {
    $taskRuns = @($results.runs | Where-Object { $_.task_id -eq $task.id })
    if ($taskRuns.Count -ne $expectedRuns) {
        throw "Task $($task.id) has $($taskRuns.Count) runs; expected $expectedRuns."
    }
    foreach ($run in $taskRuns) {
        if ([string]::IsNullOrWhiteSpace([string]$run.evidence_path) `
            -or -not (Test-Path -LiteralPath $run.evidence_path)) {
            throw "Task $($task.id) run $($run.run) has no inspectable evidence."
        }
    }
    $terminalCorrect = @(
        $taskRuns | Where-Object { $_.terminal -eq $task.expected_terminal }
    ).Count
    $proven = @($taskRuns | Where-Object { $_.terminal -eq "PROVEN" }).Count
    [ordered]@{
        task_id = $task.id
        family = $task.family
        expected_terminal = $task.expected_terminal
        runs = $taskRuns.Count
        proven_runs = $proven
        terminal_correct_runs = $terminalCorrect
    }
}

$feasible = @($catalog.tasks | Where-Object { $_.expected_terminal -eq "PROVEN" })
$feasibleIds = @($feasible | ForEach-Object { $_.id })
$feasibleRuns = @($results.runs | Where-Object { $_.task_id -in $feasibleIds })
$provenRate = if ($feasibleRuns.Count -eq 0) {
    0
} else {
    @($feasibleRuns | Where-Object { $_.terminal -eq "PROVEN" }).Count / $feasibleRuns.Count
}
$allRuns = @($results.runs)
$correctTerminals = 0
foreach ($task in $catalog.tasks) {
    $correctTerminals += @(
        $allRuns | Where-Object {
            $_.task_id -eq $task.id -and $_.terminal -eq $task.expected_terminal
        }
    ).Count
}
$terminalAccuracy = if ($allRuns.Count -eq 0) {
    0
} else {
    $correctTerminals / $allRuns.Count
}
$passed = $provenRate -ge [double]$catalog.minimum_proven_rate `
    -and $terminalAccuracy -ge [double]$catalog.minimum_terminal_accuracy

$report = [ordered]@{
    schema_version = 1
    suite = $catalog.suite
    generated_at = [DateTimeOffset]::Now.ToString("O")
    passed = $passed
    total_tasks = $catalog.tasks.Count
    total_runs = $allRuns.Count
    proven_rate = [Math]::Round($provenRate, 4)
    terminal_accuracy = [Math]::Round($terminalAccuracy, 4)
    thresholds = [ordered]@{
        minimum_proven_rate = $catalog.minimum_proven_rate
        minimum_terminal_accuracy = $catalog.minimum_terminal_accuracy
    }
    tasks = $evaluations
}
$report |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $ReportPath -Encoding UTF8

if (-not $passed) {
    throw "GA benchmark failed: PROVEN rate $([Math]::Round($provenRate * 100, 2))%, terminal accuracy $([Math]::Round($terminalAccuracy * 100, 2))%."
}

Write-Host "GA benchmark passed."
Write-Host "PROVEN rate:       $([Math]::Round($provenRate * 100, 2))%"
Write-Host "Terminal accuracy: $([Math]::Round($terminalAccuracy * 100, 2))%"
Write-Host "Report:            $ReportPath"
