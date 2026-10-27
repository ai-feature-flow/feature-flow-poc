# Restart the spike sandbox and worker after a rebuild.
# Usage: .\restart.ps1 [-WorkerArgs "--max-concurrent-activities 4"]
param(
    [string]$WorkerArgs = "",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$logDir = 'd:\Dev\feature-flow-poc\.spike'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Stop anything still holding the DLLs.
Get-Process -Name dotnet -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
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
