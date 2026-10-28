# Run one Spike 1 scenario, capture its history as a replay fixture, then
# restart the services so the next scenario starts clean.
#
# The crash tests kill the worker by design, so a shared worker cannot survive
# across scenarios. Restart between each.
#
# Usage: .\capture-one.ps1 -Name crash1-resume-after-crash -Args 'crash','1-resume-after-crash' -IdPrefix crash1
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$IdPrefix,
    [Parameter(ValueFromRemainingArguments)][string[]]$HarnessArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$harness = Join-Path $root 'src\FeatureFlow.Spike.Harness'
$env:FF_RECORD_STORE = Join-Path $root 'results\records'

Write-Host "=== restarting services ==="
& (Join-Path $root 'restart.ps1') -NoBuild | Out-Null

Write-Host "=== running $Name ==="
Push-Location $harness
try {
    $output = & dotnet run --no-build -- @HarnessArgs 2>&1 | Out-String
}
finally { Pop-Location }

$output.Trim() -split "`n" | Select-Object -Last 8 | ForEach-Object { Write-Host "  $_" }

$match = [regex]::Match($output, "$IdPrefix-\d+")
if (-not $match.Success) {
    Write-Host "  could not find a workflow id with prefix $IdPrefix"
    exit 1
}
$id = $match.Value
Write-Host "  workflow id: $id"

Write-Host "=== capturing $id ==="
Push-Location $harness
try {
    & dotnet run --no-build -- fixture capture --id $id 2>&1 | Out-String | ForEach-Object { Write-Host "  $_" }
}
finally { Pop-Location }

Write-Host "=== done: $Name -> $id ==="
