#!/usr/bin/env pwsh
# Controls a local Feature Flow installation (PowerShell 7, Windows; also runs on macOS and Linux):
# build, start, stop, update, logs. The bash equivalent is ff.sh. See docs/getting-started.md.
#
#   ff.ps1 build                       build the hosts, the sandbox client and the sandbox image
#   ff.ps1 status                      what is running, and which commit
#   ff.ps1 start                       start whatever is stopped
#   ff.ps1 stop [-All]                 stop the three hosts; -All also stops Temporal
#   ff.ps1 restart                     stop and start the three hosts
#   ff.ps1 update [-Force]             fast-forward the checkout to origin/main, build, and restart (no-op if current)
#   ff.ps1 logs [api|agent|orchestration|temporal]
#
# Settings, by environment variable:
#   FF_HOME            install folder: configuration, secrets, data, logs, clones (default ~\feature-flow-local)
#   FF_SRC             the checkout (default: the one this script is in)
#   FF_INSTALLATION    installation file in FF_HOME (default installation.yaml)
#   FF_ORGANISATION    organisation file in FF_HOME (default organisation.yaml)
#   FF_TEMPORAL        the Temporal CLI (default: temporal on PATH)
#   FF_TEMPORAL_PORT   Temporal's gRPC port (default 7233); its UI is on this plus 1000
#   FF_API_PORT        the API and console port, on 127.0.0.1 (default 8480)
#   FF_IMAGE           the sandbox image tag (default feature-flow-sandbox:local)
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Command = 'status',
    [Parameter(Position = 1)] [string] $Name = 'api',
    [switch] $All,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'

function Setting($name, $default) { $v = [Environment]::GetEnvironmentVariable($name); if ($v) { $v } else { $default } }

$HomeDir = Setting 'FF_HOME' (Join-Path $HOME 'feature-flow-local')
$Src = Setting 'FF_SRC' (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$Installation = Setting 'FF_INSTALLATION' 'installation.yaml'
$Organisation = Setting 'FF_ORGANISATION' 'organisation.yaml'
$TemporalPort = [int](Setting 'FF_TEMPORAL_PORT' '7233')
$ApiPort = [int](Setting 'FF_API_PORT' '8480')
$Image = Setting 'FF_IMAGE' 'feature-flow-sandbox:local'
$Temporal = Setting 'FF_TEMPORAL' 'temporal'
$Logs = Join-Path $HomeDir 'logs'
$DockerDir = 'src/Providers/FeatureFlow.Providers.Docker'

function Dll($host_) { Join-Path $Src 'hosts' "FeatureFlow.Host.$host_" 'bin' 'Release' 'net10.0' "FeatureFlow.Host.$host_.dll" }

function GitIn { & git -C $Src @args; if ($LASTEXITCODE) { throw "git $args failed" } }

# The dotnet process running a host's dll, if any.
function HostProcess($host_) {
    $dll = Dll $host_
    Get-CimInstance Win32_Process -Filter "Name like 'dotnet%'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($dll) } | Select-Object -First 1
}

# The process listening on Temporal's port, if any.
function TemporalProcess {
    $c = Get-NetTCPConnection -LocalPort $TemporalPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue }
}

function State($p) { if ($p) { "running, pid $($p.ProcessId ?? $p.Id)" } else { 'stopped' } }

# The runtime identifier of the machine Docker runs on: the sandbox client is a Linux binary for that architecture.
function SandboxRid {
    $arch = (& docker info --format '{{.Architecture}}' 2>$null)
    if (-not $arch) { $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() }
    switch -Regex ($arch.ToLowerInvariant()) {
        '^(aarch64|arm64)$' { return 'linux-arm64' }
        '^(x86_64|amd64|x64)$' { return 'linux-x64' }
        default { throw "unsupported architecture: $arch" }
    }
}

function Status {
    $subject = [string](GitIn log -1 --format=%s)
    "checkout  $Src  $(GitIn rev-parse --short HEAD) ($($subject.Substring(0, [Math]::Min(60, $subject.Length))))"
    $behind = & git -C $Src rev-list --count HEAD..origin/main 2>$null
    if (-not $behind) { $behind = '?' }
    "          $behind commit(s) behind origin/main (as last fetched)"
    "install   $HomeDir  ($Installation, $Organisation)"
    foreach ($h in 'OrchestrationWorker', 'AgentWorker', 'Api') { '{0,-22} {1}' -f $h, (State (HostProcess $h)) }
    '{0,-22} {1}' -f "Temporal ($TemporalPort)", (State (TemporalProcess))
    "console   http://127.0.0.1:$ApiPort/console    Temporal UI http://127.0.0.1:$($TemporalPort + 1000)"
}

# Start a process detached, its output in two log files (a process cannot send both streams to one file here).
function StartDetached($file, $arguments, $log) {
    Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $HomeDir -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $Logs "$log.log") -RedirectStandardError (Join-Path $Logs "$log.err.log") | Out-Null
}

function StartTemporal {
    if (-not (TemporalProcess)) {
        'starting Temporal'
        StartDetached $Temporal @('server', 'start-dev', '--headless', '--port', $TemporalPort, '--http-port', ($TemporalPort + 1),
            '--metrics-port', ($TemporalPort + 2), '--ui-port', ($TemporalPort + 1000), '--db-filename', 'data/temporal.db') 'temporal'
        Start-Sleep 3
    }
}

function StartHost($host_, $log) {
    $dll = Dll $host_
    if (-not (Test-Path $dll)) { throw "$dll is missing: run ff.ps1 build" }
    if (-not (HostProcess $host_)) {
        "starting $host_"
        # FF_API_LISTEN is read by the API host alone.
        $env:FF_API_LISTEN = "http://127.0.0.1:$ApiPort"
        StartDetached 'dotnet' @($dll, '--installation', $Installation, '--organisation', $Organisation) $log
    }
}

function Start-All {
    if (-not (Test-Path (Join-Path $HomeDir $Installation))) { throw "$(Join-Path $HomeDir $Installation) is missing: see docs/getting-started.md section 5" }
    New-Item -ItemType Directory -Force (Join-Path $HomeDir 'data'), $Logs | Out-Null
    StartTemporal
    StartHost 'OrchestrationWorker' 'orchestration'
    StartHost 'AgentWorker' 'agent'
    StartHost 'Api' 'api'
    Start-Sleep 5
    Status
}

function Stop-All {
    foreach ($h in 'Api', 'AgentWorker', 'OrchestrationWorker') {
        $p = HostProcess $h
        if ($p) { "stopping $h ($($p.ProcessId))"; Stop-Process -Id $p.ProcessId; Wait-Process -Id $p.ProcessId -Timeout 20 -ErrorAction SilentlyContinue }
    }
    if ($All) {
        $p = TemporalProcess
        if ($p) { "stopping Temporal ($($p.Id))"; Stop-Process -Id $p.Id; Wait-Process -Id $p.Id -Timeout 20 -ErrorAction SilentlyContinue }
    }
}

function Build {
    $rid = SandboxRid
    "building $(GitIn rev-parse --short HEAD)"
    Push-Location $Src
    try {
        & dotnet build FeatureFlow.slnx -c Release -v q -nologo; if ($LASTEXITCODE) { throw 'dotnet build failed' }
        "publishing the sandbox client for $rid"
        & dotnet publish src/FeatureFlow.SandboxClient -c Release -r $rid --self-contained -p:PublishSingleFile=true `
            -o (Join-Path $HomeDir 'sandbox-client') -v q -nologo
        if ($LASTEXITCODE) { throw 'dotnet publish failed' }
        "building the sandbox image $Image"
        & docker build -q -t $Image -f "$DockerDir/sandbox.Dockerfile" $DockerDir; if ($LASTEXITCODE) { throw 'docker build failed' }
    }
    finally { Pop-Location }
}

function Update {
    GitIn fetch -q origin
    $old = GitIn rev-parse HEAD
    GitIn merge -q --ff-only origin/main
    $new = GitIn rev-parse HEAD
    if ($old -eq $new -and -not $Force) { "already at origin/main ($(GitIn rev-parse --short HEAD))"; return }
    Stop-All
    Build
    Start-All
}

switch ($Command) {
    'status' { Status }
    'start' { Start-All }
    'stop' { Stop-All }
    'restart' { Stop-All; Start-Sleep 2; Start-All }
    'build' { Build }
    'update' { Update }
    'logs' {
        $file = Join-Path $Logs "$Name.log"
        Get-Content $file -Tail 50 -Wait
    }
    default { Write-Error 'usage: ff.ps1 status | start | stop [-All] | restart | build | update [-Force] | logs [api|agent|orchestration|temporal]'; exit 2 }
}
