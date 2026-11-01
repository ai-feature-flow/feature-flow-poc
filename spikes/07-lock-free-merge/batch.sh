#!/bin/bash
# Runs the remaining SP4 runs in sequence. Usage: batch.sh <work dir>
work="$1"
cd "$(dirname "$0")"
for spec in "S1 2 main" "S1 3 main" "S2 1 main" "S2 2 main" "S2 3 main" "S3 1 main" "S3 2 main" "S1 1 n1" "S1 1 n2"; do
  read -r scenario run variant <<< "$spec"
  python3 merge.py "$scenario" "$run" --variant "$variant" --work "$work" 2>&1 >/dev/null | grep "^SP4\|Error"
done
