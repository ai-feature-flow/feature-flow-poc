#!/usr/bin/env bash
# Controls a local Feature Flow installation (macOS and Linux): build, start, stop, update, logs.
# The Windows equivalent is ff.ps1. See docs/getting-started.md.
#
#   ff.sh build                       build the hosts, the sandbox client and the sandbox image
#   ff.sh status                      what is running, and which commit
#   ff.sh start                       start whatever is stopped
#   ff.sh stop [--all]                stop the three hosts; --all also stops Temporal
#   ff.sh restart                     stop and start the three hosts
#   ff.sh update [--force]            fast-forward the checkout to origin/main, build, and restart (no-op if current)
#   ff.sh logs [api|agent|orchestration|temporal]
#
# Settings, by environment variable:
#   FF_HOME            install folder: configuration, secrets, data, logs, clones (default ~/feature-flow-local)
#   FF_SRC             the checkout (default: the one this script is in)
#   FF_INSTALLATION    installation file in FF_HOME (default installation.yaml)
#   FF_ORGANISATION    organisation file in FF_HOME (default organisation.yaml)
#   FF_TEMPORAL        the Temporal CLI (default: temporal on PATH, else ~/.temporalio/bin/temporal)
#   FF_TEMPORAL_PORT   Temporal's gRPC port (default 7233); its UI is on this plus 1000
#   FF_API_PORT        the API and console port, on 127.0.0.1 (default 8480)
#   FF_IMAGE           the sandbox image tag (default feature-flow-sandbox:local)
set -euo pipefail

HOME_DIR="${FF_HOME:-$HOME/feature-flow-local}"
SRC="${FF_SRC:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
INSTALLATION="${FF_INSTALLATION:-installation.yaml}"
ORGANISATION="${FF_ORGANISATION:-organisation.yaml}"
TEMPORAL_PORT="${FF_TEMPORAL_PORT:-7233}"
API_PORT="${FF_API_PORT:-8480}"
IMAGE="${FF_IMAGE:-feature-flow-sandbox:local}"
LOGS="$HOME_DIR/logs"
DOCKER_DIR="src/Providers/FeatureFlow.Providers.Docker"
if [ -n "${FF_TEMPORAL:-}" ]; then TEMPORAL="$FF_TEMPORAL"
elif command -v temporal >/dev/null 2>&1; then TEMPORAL="temporal"
else TEMPORAL="$HOME/.temporalio/bin/temporal"; fi

dll() { echo "$SRC/hosts/FeatureFlow.Host.$1/bin/Release/net10.0/FeatureFlow.Host.$1.dll"; }
pid_of() { pgrep -f "^dotnet $1" 2>/dev/null | head -1 || true; }
temporal_pid() { pgrep -f "server start-dev .*--port $TEMPORAL_PORT( |\$)" 2>/dev/null | head -1 || true; }
state() { if [ -n "$1" ]; then echo "running, pid $1"; else echo "stopped"; fi; }

# The runtime identifier of the machine Docker runs on: the sandbox client is a Linux binary for that architecture.
sandbox_rid() {
  local arch
  arch=$(docker info --format '{{.Architecture}}' 2>/dev/null || uname -m)
  case "$arch" in
    aarch64|arm64) echo "linux-arm64" ;;
    x86_64|amd64) echo "linux-x64" ;;
    *) echo "unsupported architecture: $arch" >&2; return 1 ;;
  esac
}

status() {
  echo "checkout  $SRC  $(git -C "$SRC" rev-parse --short HEAD) ($(git -C "$SRC" log -1 --format=%s | cut -c1-60))"
  local behind
  behind=$(git -C "$SRC" rev-list --count HEAD..origin/main 2>/dev/null || echo "?")
  echo "          $behind commit(s) behind origin/main (as last fetched)"
  echo "install   $HOME_DIR  ($INSTALLATION, $ORGANISATION)"
  local h
  for h in OrchestrationWorker AgentWorker Api; do
    printf '%-22s %s\n' "$h" "$(state "$(pid_of "$(dll "$h")")")"
  done
  printf '%-22s %s\n' "Temporal ($TEMPORAL_PORT)" "$(state "$(temporal_pid)")"
  echo "console   http://127.0.0.1:$API_PORT/console    Temporal UI http://127.0.0.1:$((TEMPORAL_PORT + 1000))"
}

start_temporal() {
  if [ -z "$(temporal_pid)" ]; then
    echo "starting Temporal"
    (cd "$HOME_DIR" && exec nohup "$TEMPORAL" server start-dev --headless --port "$TEMPORAL_PORT" --http-port "$((TEMPORAL_PORT + 1))" \
      --metrics-port "$((TEMPORAL_PORT + 2))" --ui-port "$((TEMPORAL_PORT + 1000))" --db-filename data/temporal.db) \
      >>"$LOGS/temporal.log" 2>&1 &
    sleep 3
  fi
}

# start_host <host> <log name>: start one host in the install folder unless it is running.
start_host() {
  local file
  file=$(dll "$1")
  [ -f "$file" ] || { echo "$file is missing: run ff.sh build" >&2; exit 1; }
  if [ -z "$(pid_of "$file")" ]; then
    echo "starting $1"
    # FF_API_LISTEN is read by the API host alone.
    (cd "$HOME_DIR" && FF_API_LISTEN="http://127.0.0.1:$API_PORT" exec nohup dotnet "$file" \
      --installation "$INSTALLATION" --organisation "$ORGANISATION") >>"$LOGS/$2.log" 2>&1 &
  fi
}

start() {
  [ -f "$HOME_DIR/$INSTALLATION" ] || { echo "$HOME_DIR/$INSTALLATION is missing: see docs/getting-started.md section 5" >&2; exit 1; }
  mkdir -p "$HOME_DIR/data" "$LOGS"
  start_temporal
  start_host OrchestrationWorker orchestration
  start_host AgentWorker agent
  start_host Api api
  sleep 5
  status
}

# wait_gone <pid>: up to 20 seconds for a process to exit.
wait_gone() {
  local i
  for i in $(seq 1 20); do kill -0 "$1" 2>/dev/null || return 0; sleep 1; done
  echo "pid $1 is still running" >&2
}

stop() {
  local h p
  for h in Api AgentWorker OrchestrationWorker; do
    p=$(pid_of "$(dll "$h")")
    [ -z "$p" ] || { echo "stopping $h ($p)"; kill "$p"; wait_gone "$p"; }
  done
  if [ "${1:-}" = "--all" ]; then
    p=$(temporal_pid)
    [ -z "$p" ] || { echo "stopping Temporal ($p)"; kill "$p"; wait_gone "$p"; }
  fi
}

build() {
  local rid
  rid=$(sandbox_rid)
  echo "building $(git -C "$SRC" rev-parse --short HEAD)"
  (cd "$SRC" && dotnet build FeatureFlow.slnx -c Release -v q -nologo)
  echo "publishing the sandbox client for $rid"
  (cd "$SRC" && dotnet publish src/FeatureFlow.SandboxClient -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -o "$HOME_DIR/sandbox-client" -v q -nologo)
  echo "building the sandbox image $IMAGE"
  (cd "$SRC" && docker build -q -t "$IMAGE" -f "$DOCKER_DIR/sandbox.Dockerfile" "$DOCKER_DIR")
}

update() {
  git -C "$SRC" fetch -q origin
  local old new
  old=$(git -C "$SRC" rev-parse HEAD)
  git -C "$SRC" merge -q --ff-only origin/main
  new=$(git -C "$SRC" rev-parse HEAD)
  if [ "$old" = "$new" ] && [ "${1:-}" != "--force" ]; then
    echo "already at origin/main ($(git -C "$SRC" rev-parse --short HEAD))"
    return
  fi
  stop
  build
  start
}

case "${1:-status}" in
  status) status ;;
  start) start ;;
  stop) shift; stop "${1:-}" ;;
  restart) stop; sleep 2; start ;;
  build) build ;;
  update) shift; update "${1:-}" ;;
  logs) tail -n 50 -f "$LOGS/${2:-api}.log" ;;
  *) echo "usage: ff.sh status | start | stop [--all] | restart | build | update [--force] | logs [api|agent|orchestration|temporal]"; exit 2 ;;
esac
