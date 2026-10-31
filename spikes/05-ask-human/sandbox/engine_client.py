"""The client in the sandbox: Claude Code's stdio MCP server for ask_human and report.

It forwards each call to an agent worker replica over HTTP. The question id is
assigned here, once, so retrying against another replica resumes the same
question rather than opening a new one (docs/02 section 4.2).

Environment:
  STEP_ID      the step's workflow id
  REPLICAS     comma-separated replica base URLs
  CLIENT_LOG   path of the JSON-lines log
  CLIENT_MODE  "reconnect" (default) or "noreconnect" (negative control N1)
"""
import http.client
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

STEP_ID = os.environ["STEP_ID"]
REPLICAS = os.environ["REPLICAS"].split(",")
LOG = os.environ["CLIENT_LOG"]
MODE = os.environ.get("CLIENT_MODE", "reconnect")
MAX_FAILING_SECONDS = 600

out_lock = threading.Lock()
replica_index = 0


def log(event, **detail):
    with open(LOG, "a") as f:
        f.write(json.dumps({"t": time.time(), "event": event, **detail}) + "\n")


def send(message):
    with out_lock:
        sys.stdout.write(json.dumps(message) + "\n")
        sys.stdout.flush()


class Unreachable(Exception):
    pass


def call(method, path, body=None, timeout=10):
    """One request to the current replica. Connection errors raise Unreachable."""
    url = REPLICAS[replica_index] + path
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read())
    except (urllib.error.URLError, http.client.HTTPException, ConnectionError, TimeoutError, OSError) as e:
        raise Unreachable(f"{type(e).__name__}: {e}") from e


def with_failover(method, path, body=None, timeout=10):
    """Retry across replicas until one answers, for up to MAX_FAILING_SECONDS."""
    global replica_index
    failing_since = None
    while True:
        try:
            result = call(method, path, body, timeout)
            if failing_since is not None:
                log("reconnected", replica=REPLICAS[replica_index], pid=result.get("pid"),
                    after_seconds=round(time.time() - failing_since, 1))
            return result
        except Unreachable as e:
            log("connection_failed", replica=REPLICAS[replica_index], path=path, error=str(e))
            if MODE == "noreconnect":
                raise
            failing_since = failing_since or time.time()
            if time.time() - failing_since > MAX_FAILING_SECONDS:
                raise
            replica_index = (replica_index + 1) % len(REPLICAS)
            time.sleep(1)


def ask_human(arguments):
    question_id = str(uuid.uuid4())
    log("ask", question_id=question_id, question=arguments.get("question"))
    opened = with_failover("POST", "/questions", {
        "stepId": STEP_ID, "questionId": question_id,
        "text": arguments.get("question", ""), "options": arguments.get("options", []),
    })
    log("opened", question_id=question_id, replica=REPLICAS[replica_index], pid=opened.get("pid"))
    while True:
        state = with_failover("GET", f"/questions/{STEP_ID}/{question_id}?wait=25", timeout=35)
        if state["status"] == "answered":
            log("answer_received", question_id=question_id, answer=state["answer"],
                replica=REPLICAS[replica_index], pid=state.get("pid"))
            return f"The person answered: {state['answer']}"
        if state["status"] == "timed_out":
            log("timed_out_received", question_id=question_id, pid=state.get("pid"))
            return "No answer came before the timeout. Use your judgement and carry on."


def report(arguments):
    with_failover("POST", "/report", {
        "stepId": STEP_ID, "outcome": arguments.get("outcome", ""), "summary": arguments.get("summary", ""),
    })
    log("reported", outcome=arguments.get("outcome"))
    return "Report recorded."


TOOLS = [
    {
        "name": "ask_human",
        "description": "Ask a person a question and wait for their answer. May take a long time.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "question": {"type": "string"},
                "options": {"type": "array", "items": {"type": "string"}, "maxItems": 4},
            },
            "required": ["question"],
        },
    },
    {
        "name": "report",
        "description": "Report the step's result: one outcome and a short summary.",
        "inputSchema": {
            "type": "object",
            "properties": {"outcome": {"type": "string"}, "summary": {"type": "string"}},
            "required": ["outcome"],
        },
    },
]
HANDLERS = {"ask_human": ask_human, "report": report}


def handle_call(message_id, params):
    name = params.get("name")
    try:
        text = HANDLERS[name](params.get("arguments") or {})
        send({"jsonrpc": "2.0", "id": message_id,
              "result": {"content": [{"type": "text", "text": text}], "isError": False}})
    except Exception as e:  # returned to the agent as a tool error
        log("tool_error", tool=name, error=f"{type(e).__name__}: {e}")
        send({"jsonrpc": "2.0", "id": message_id,
              "result": {"content": [{"type": "text", "text": f"engine unreachable: {e}"}], "isError": True}})


def main():
    log("started", mode=MODE, replicas=REPLICAS)
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
            send({"jsonrpc": "2.0", "id": message_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            # A thread, so pings and other requests are answered during a long wait.
            threading.Thread(target=handle_call, args=(message_id, message["params"]), daemon=True).start()
        elif method == "ping":
            send({"jsonrpc": "2.0", "id": message_id, "result": {}})
        elif message_id is not None:
            send({"jsonrpc": "2.0", "id": message_id,
                  "error": {"code": -32601, "message": f"method not found: {method}"}})


if __name__ == "__main__":
    main()
