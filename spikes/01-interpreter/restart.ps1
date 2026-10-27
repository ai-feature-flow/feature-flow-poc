# Restart the spike sandbox and worker after a rebuild.
# Usage: .\restart.ps1 [-WorkerArgs "--max-concurrent-activities 4"]
param(
    [string]$WorkerArgs = "",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$logDir = Join-Path $root '.logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Review 12 A1: step-run detail, patch records and history samples go to the
# record store, not to carried state. Both the worker and the harness need the
# same path, so set it here rather than relying on the caller's environment.
$recordStore = Join-Path $root 'results\records'
New-Item -ItemType Directory -Force -Path $recordStore | Out-Null
$env:FF_RECORD_STORE = $recordStore

# Review 12 B3: stop only the spike's own processes, not every dotnet process on
# the machine. The sandbox and worker are started with `dotnet run` from their
# own directories, so match on the --urls and --queue arguments they were given.
$spikePids = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object {
        $_.CommandLine -like '*--urls http://127.0.0.1:5199*' -or
        $_.CommandLine -like '*--queue spike-1*'
    } |
    Select-Object -ExpandProperty ProcessId

foreach ($spikePid in $spikePids) {
    Write-Host "stopping spike process $spikePid"
    Stop-Process -Id $spikePid -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

if (-not $NoBuild) {
    Push-Location $src
    dotnet build FeatureFlow.Spike.sln -v q --nologo
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "build failed" }
    Pop-Location
}

$sandbox = Join-Path $src 'FeatureFlow.Spike.Sandbox'
Start-Process -FilePath dotnet -ArgumentList 'run','--no-build','--urls','http://127.0.0.1:5199' `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDir 'sandbox.log') `
    -RedirectStandardError (Join-Path $logDir 'sandbox.log.err') `
    -WorkingDirectory $sandbox

Start-Sleep -Seconds 6
try {
    $health = Invoke-WebRequest -Uri 'http://127.0.0.1:5199/health' -UseBasicParsing -TimeoutSec 10
    Write-Host "sandbox: $($health.Content)"
} catch {
    throw "sandbox did not start: $_"
}

$worker = Join-Path $src 'FeatureFlow.Spike.Worker'
$args = @('run','--no-build','--','--queue','spike-1','--sandbox','http://127.0.0.1:5199')
if ($WorkerArgs) { $args += $WorkerArgs.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) }

Start-Process -FilePath dotnet -ArgumentList $args `
    -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logDir 'worker.log') `
    -RedirectStandardError (Join-Path $logDir 'worker.log.err') `
    -WorkingDirectory $worker

Start-Sleep -Seconds 8
Write-Host "--- worker ---"
Get-Content (Join-Path $logDir 'worker.log') -Tail 5
