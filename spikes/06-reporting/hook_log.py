"""A PreToolUse hook that only records that it fired (X1). Always allows."""
import json
import os
import sys
import time

with open(os.environ["HOOK_LOG"], "a") as f:
    f.write(json.dumps({"t": time.time(), "input": sys.stdin.read()[:2000]}) + "\n")
sys.exit(0)
