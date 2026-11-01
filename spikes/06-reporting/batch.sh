#!/bin/bash
# Runs every SP3 trial not yet run for one runtime. Usage: batch.sh <claude|copilot> <work dir>
runtime="$1"
work="$2"
cd "$(dirname "$0")"
trials() {
  local task="$1" from="$2" to="$3"
  for n in $(seq "$from" "$to"); do
    python3 run.py "$runtime" "$task" "$n" --work "$work"
  done
}
if [ "$runtime" = claude ]; then trials T1 2 5; trials T2 2 5; else trials T1 2 5; trials T2 1 5; fi
trials T3 1 5
trials T4 1 5
trials N1 1 2
trials N2 1 2
