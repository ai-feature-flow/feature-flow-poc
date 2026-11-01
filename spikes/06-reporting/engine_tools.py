"""The engine's tools for one agent step, as a stdio MCP server: report and ask_human.

Validates each report against the step's contract (docs/spikes/sp3-results.md
section 2) and returns errors as correction turns. Logs every call.

Environment:
  TOOL_LOG      JSON-lines log path
  SANDBOX       the agent's working directory, for checking artifacts
  TOOLS_MODE    "normal", "inject_first" (T2) or "reject_all" (control N2)
  QUESTION_FILE written when ask_human opens a question
  ANSWER_FILE   ask_human returns its content once it exists
"""
import json
import os
import sys
import threading
import time

LOG = os.environ["TOOL_LOG"]
SANDBOX = os.environ["SANDBOX"]
MODE = os.environ.get("TOOLS_MODE", "normal")
QUESTION_FILE = os.environ.get("QUESTION_FILE", "")
ANSWER_FILE = os.environ.get("ANSWER_FILE", "")
CORRECTION_TURNS = 2
TICKET_PREFIX = "FF-142: "

out_lock = threading.Lock()
state = {"rejections": 0, "accepted": False, "failed": False}


def log(event, **detail):
    with open(LOG, "a") as f:
        f.write(json.dumps({"t": time.time(), "event": event, **detail}) + "\n")


def send(message):
    with out_lock:
        sys.stdout.write(json.dumps(message) + "\n")
        sys.stdout.flush()


def contract_errors(report):
    errors = []
    if report.get("outcome") not in ("implemented", "blocked"):
        errors.append('`outcome` must be one of "implemented", "blocked"')
    summary = report.get("summary")
    if not isinstance(summary, str) or not summary.strip():
        errors.append("`summary` must be a non-empty string")
    elif len(summary.encode()) > 2048:
        errors.append("`summary` must be at most 2 KB")
    outputs = report.get("outputs")
    if not isinstance(outputs, dict):
        errors.append("`outputs` must be an object")
    else:
        files_changed = outputs.get("files_changed")
        if not isinstance(files_changed, int) or isinstance(files_changed, bool):
            errors.append("`outputs.files_changed` must be an integer")
        if outputs.get("risk") not in ("low", "medium", "high"):
            errors.append('`outputs.risk` must be one of "low", "medium", "high"')
    artifacts = report.get("artifacts")
    if not isinstance(artifacts, list) or "notes.md" not in artifacts:
        errors.append("`artifacts` must list notes.md")
    else:
        for path in artifacts:
            full = os.path.join(SANDBOX, str(path))
            if not os.path.isfile(full) or os.path.getsize(full) == 0:
                errors.append(f"artifact {path} does not exist or is empty")
    return errors


def report(arguments):
    number = state.get("calls", 0) + 1
    state["calls"] = number
    if state["failed"]:
        log("report", call=number, verdict="after_failure", arguments=arguments)
        return "The attempt has already failed. Stop now."

    errors = contract_errors(arguments)
    if MODE == "inject_first":
        summary = arguments.get("summary") or ""
        if number == 1:
            errors = [f"`summary` must begin with the ticket id `{TICKET_PREFIX}`"]
        elif isinstance(summary, str) and not summary.startswith(TICKET_PREFIX):
            errors.append(f"`summary` must begin with the ticket id `{TICKET_PREFIX}`")
    elif MODE == "reject_all":
        errors = ["`summary` must be empty and must also be a non-empty string"]

    if not errors:
        state["accepted"] = True
        log("report", call=number, verdict="accepted", arguments=arguments)
        return "Report accepted. The step is complete; stop now."

    state["rejections"] += 1
    if state["rejections"] > CORRECTION_TURNS:
        state["failed"] = True
        log("report", call=number, verdict="attempt_failed", errors=errors, arguments=arguments)
        return "Report rejected and no correction turns remain: the attempt has failed. Stop now; do not call report again."
    log("report", call=number, verdict="rejected", errors=errors, arguments=arguments)
    return (f"Report rejected (correction turn {state['rejections']} of {CORRECTION_TURNS}). "
            "Fix these and call report again:\n- " + "\n- ".join(errors))


def ask_human(arguments):
    question = arguments.get("question", "")
    log("ask", question=question)
    with open(QUESTION_FILE, "w") as f:
        f.write(question)
    while not os.path.exists(ANSWER_FILE):
        time.sleep(0.2)
    with open(ANSWER_FILE) as f:
        answer = f.read().strip()
    log("answer_returned", answer=answer)
    return f"The person answered: {answer}"


TOOLS = [
    {
        "name": "report",
        "description": "Report the step's result to the engine. The engine validates it and may ask for corrections.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "outcome": {"type": "string"},
                "summary": {"type": "string"},
                "outputs": {"type": "object"},
                "artifacts": {"type": "array", "items": {"type": "string"}},
            },
            "required": ["outcome", "summary", "outputs", "artifacts"],
        },
    },
    {
        "name": "ask_human",
        "description": "Ask a person a question and wait for their answer. May take a long time.",
        "inputSchema": {
            "type": "object",
            "properties": {"question": {"type": "string"}},
            "required": ["question"],
        },
    },
]
HANDLERS = {"report": report, "ask_human": ask_human}


def handle_call(message_id, params):
    name = params.get("name")
    try:
        text = HANDLERS[name](params.get("arguments") or {})
        send({"jsonrpc": "2.0", "id": message_id,
              "result": {"content": [{"type": "text", "text": text}], "isError": False}})
    except Exception as e:
        log("tool_error", tool=name, error=f"{type(e).__name__}: {e}")
        send({"jsonrpc": "2.0", "id": message_id,
              "result": {"content": [{"type": "text", "text": f"tool error: {e}"}], "isError": True}})


def main():
    log("started", mode=MODE)
    for line in sys.stdin:
        if not line.strip():
            continue
        message = json.loads(line)
        method, message_id = message.get("method"), message.get("id")
        if method == "initialize":
            send({"jsonrpc": "2.0", "id": message_id, "result": {
                "protocolVersion": message["params"].get("protocolVersion", "2025-06-18"),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "engine", "version": "0.1"},
            }})
        elif method == "tools/list":
            log("tools_listed")
            send({"jsonrpc": "2.0", "id": message_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            threading.Thread(target=handle_call, args=(message_id, message["params"]), daemon=True).start()
        elif method == "ping":
            send({"jsonrpc": "2.0", "id": message_id, "result": {}})
        elif message_id is not None:
            send({"jsonrpc": "2.0", "id": message_id,
                  "error": {"code": -32601, "message": f"method not found: {method}"}})


if __name__ == "__main__":
    main()
