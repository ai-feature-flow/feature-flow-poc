# Run every Spike 1 scenario and capture its history as a committed replay
# fixture. Review 12 A6: the replay suite must cover the failure paths, not just
# the linear runs, and must survive a dev server reset.
#
# Usage: .\capture-fixtures.ps1
param([switch]$SkipLong)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$harness = Join-Path $root 'src\FeatureFlow.Spike.Harness'
$env:FF_RECORD_STORE = Join-Path $root 'results\records'

function Invoke-Harness {
    param([string[]]$Arguments)
    Push-Location $harness
    try {
        $output = & dotnet run --no-build -- @Arguments 2>&1 | Out-String
        return $output
    }
    finally { Pop-Location }
}

function Get-WorkflowId {
    param([string]$Output, [string]$Prefix)
    $match = [regex]::Match($Output, "$Prefix-(\d+)")
    if ($match.Success) { return $match.Value }
    return $null
}

$ids = @{}

Write-Host "=== crash 1: resume after crash ==="
$out = Invoke-Harness @('crash', '1-resume-after-crash')
$out.Trim() -split "`n" | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'crash1'
if ($id) { $ids['crash1-resume-after-crash'] = $id; Write-Host "  id=$id" }

Write-Host "=== crash 3: hook unreachable ==="
$out = Invoke-Harness @('crash', '3-hook-unreachable-infrastructure')
$out.Trim() -split "`n" | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'crash3'
if ($id) { $ids['crash3-hook-unreachable'] = $id; Write-Host "  id=$id" }

Write-Host "=== crash 4: break connection ==="
$out = Invoke-Harness @('crash', '4-break-connection')
$out.Trim() -split "`n" | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'crash4'
if ($id) { $ids['crash4-break-connection'] = $id; Write-Host "  id=$id" }

Write-Host "=== patch mid-flight ==="
$out = Invoke-Harness @('patch')
$out.Trim() -split "`n" | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'patch-midrun'
if ($id) { $ids['patch-mid-flight'] = $id; Write-Host "  id=$id" }

Write-Host "=== continue-as-new window ==="
$out = Invoke-Harness @('can', '--attempts', '20')
$out.Trim() -split "`n" | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'can'
if ($id) { $ids['continue-as-new-window'] = $id; Write-Host "  id=$id" }

Write-Host "=== history with continue-as-new ==="
$out = Invoke-Harness @('history', '--steps', '120')
$out.Trim() -split "`n" | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
$id = Get-WorkflowId $out 'history'
if ($id) { $ids['history-continue-as-new'] = $id; Write-Host "  id=$id" }

if (-not $SkipLong) {
    Write-Host "=== backoff: long outage ==="
    $out = Invoke-Harness @('backoff', '--seconds', '700')
    $out.Trim() -split "`n" | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    $id = Get-WorkflowId $out 'backoff'
    if ($id) { $ids['backoff-long-outage'] = $id; Write-Host "  id=$id" }

    Write-Host "=== outage: Temporal server down ==="
    $out = Invoke-Harness @('outage', '--seconds', '60')
    $out.Trim() -split "`n" | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    $id = Get-WorkflowId $out 'outage'
    if ($id) { $ids['temporal-outage'] = $id; Write-Host "  id=$id" }
}

Write-Host ""
Write-Host "=== capturing histories ==="
foreach ($name in $ids.Keys) {
    $out = Invoke-Harness @('fixture', 'capture', '--id', $ids[$name])
    Write-Host "  $name -> $($ids[$name])"
}

Write-Host ""
Write-Host "=== replaying all fixtures ==="
$out = Invoke-Harness @('replay-fixtures')
Write-Host $out
