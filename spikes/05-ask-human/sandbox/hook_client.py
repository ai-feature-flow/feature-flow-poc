"""The PreToolUse hook command in the sandbox.

Asks a replica whether the tool call is allowed. If no replica answers, it holds
and retries for up to HOLD_SECONDS, then stops the session in a resumable state.
An unreachable endpoint is never a denial (risk R19, docs/02 section 4.2).

Environment: STEP_ID, REPLICAS, HOOK_LOG, HOOK_MODE ("hold" default, or
"failclosed" for negative control N2).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

STEP_ID = os.environ["STEP_ID"]
REPLICAS = os.environ["REPLICAS"].split(",")
LOG = os.environ["HOOK_LOG"]
MODE = os.environ.get("HOOK_MODE", "hold")
HOLD_SECONDS = 120


def log(event, **detail):
    with open(LOG, "a") as f:
        f.write(json.dumps({"t": time.time(), "event": event, **detail}) + "\n")


def main():
    hook_input = json.load(sys.stdin)
    body = json.dumps({
        "stepId": STEP_ID,
        "toolName": hook_input.get("tool_name", ""),
        "toolInput": hook_input.get("tool_input", {}),
    }).encode()
    tool = hook_input.get("tool_name", "")
    started = time.time()
    index = 0
    while time.time() - started < HOLD_SECONDS:
        url = REPLICAS[index] + "/hooks/pre_tool_use"
        try:
            request = urllib.request.Request(url, data=body, method="POST",
                                             headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=10) as response:
                decision = json.loads(response.read())
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
            log("attempt_failed", tool=tool, replica=REPLICAS[index], error=f"{type(e).__name__}: {e}")
            if MODE == "failclosed":
                print("engine unreachable", file=sys.stderr)
                sys.exit(2)
            index = (index + 1) % len(REPLICAS)
            time.sleep(1)
            continue
        log("decision", tool=tool, decision=decision["decision"], pid=decision.get("pid"),
            after_seconds=round(time.time() - started, 1))
        if decision["decision"] == "deny":
            print(decision.get("reason", "denied"), file=sys.stderr)
            sys.exit(2)
        sys.exit(0)

    log("hold_exhausted", tool=tool)
    print(json.dumps({"continue": False, "stopReason": "engine unreachable; the step can be resumed"}))
    sys.exit(0)


if __name__ == "__main__":
    main()
