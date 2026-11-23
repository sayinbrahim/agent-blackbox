# Measures wall-clock time of `bbx hook` as Claude Code runs it: a fresh process per event, payload on stdin.
# Usage: pwsh scripts/bench-hook.ps1 -Bbx out/bbx [-Runs 50]
param(
    [Parameter(Mandatory)][string]$Bbx,
    [int]$Runs = 50
)
$ErrorActionPreference = 'Stop'

$bbxPath = (Resolve-Path $Bbx).Path
if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    if (-not $bbxPath.EndsWith('.exe') -and (Test-Path "$bbxPath.exe")) { $bbxPath = "$bbxPath.exe" }
}
$payload = Get-Content -Raw ([IO.Path]::Combine($PSScriptRoot, '..', 'fixtures', 'hook-events', 'PostToolUse.bash-test.json'))

$project = Join-Path ([IO.Path]::GetTempPath()) ("bbx-bench-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $project | Out-Null
Push-Location $project
try {
    & $bbxPath init | Out-Null
    $env:CLAUDE_PROJECT_DIR = $project

    1..3 | ForEach-Object { $payload | & $bbxPath hook PostToolUse }   # warm the file cache
    $times = 1..$Runs | ForEach-Object {
        (Measure-Command { $payload | & $bbxPath hook PostToolUse }).TotalMilliseconds
    } | Sort-Object

    $median = $times[[int]($times.Count / 2)]
    $p95 = $times[[int][Math]::Ceiling($times.Count * 0.95) - 1]
    "bbx hook: runs=$Runs median=$([Math]::Round($median, 1)) ms p95=$([Math]::Round($p95, 1)) ms max=$([Math]::Round($times[-1], 1)) ms"

    # The recordings must still form a valid chain.
    & $bbxPath verify
    if ($LASTEXITCODE -ne 0) { throw "verify failed" }
    if (Test-Path .blackbox/errors.log) { Get-Content .blackbox/errors.log; throw "hook wrote errors" }
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $project -ErrorAction SilentlyContinue
}
