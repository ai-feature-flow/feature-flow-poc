#!/usr/bin/env python3
"""A local stand-in for an Anthropic-compatible model router.

Spike 2 needs the *real* `claude` CLI to run so the agent runtime provider can
be verified against real process behaviour: start without waiting, poll, read a
result, resume a session, and classify a timeout. No Anthropic credential is
available on this machine, so this server implements just enough of the
Messages API for the CLI to complete a turn.

It is a test double for the *model*, not for the runtime. Everything the
provider does -- spawning `claude`, parsing its stream-json, resuming by
session id, killing it on timeout -- is real.

Endpoints
    POST /v1/messages            Anthropic Messages API (streaming and not)
    POST /v1/messages/count_tokens
    GET  /healthz
    POST /control/delay?ms=N     set an artificial per-response delay
    POST /control/reset          clear counters and the delay
    GET  /control/stats          request count, last request, seen tool names

Usage
    python mock-model-router.py [--port 8791]
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote

MODEL = "mock-claude-sonnet-4-6"

_state = {
    "requests": 0,
    "delay_ms": 0,
    "last_model": None,
    "last_system_chars": 0,
    "last_user_text": None,
    "tool_names": [],
    "tool_call": None,   # when set, the reply is a tool_use block of this name
    "tool_command": "git rev-parse --abbrev-ref HEAD",
    "tool_input": None,  # raw JSON input override, for non-Bash tools
    "started": time.time(),
}
_lock = threading.Lock()


def _reply_text(body: dict) -> str:
    """A short, deterministic reply that echoes what the runtime sent.

    Echoing matters: it is how the harness proves the prompt, the model and the
    system prompt actually reached the model endpoint, rather than being
    swallowed by the CLI.
    """
    messages = body.get("messages") or []
    last_user = ""
    for message in reversed(messages):
        if message.get("role") == "user":
            content = message.get("content")
            if isinstance(content, str):
                last_user = content
            elif isinstance(content, list):
                parts = [
                    block.get("text", "")
                    for block in content
                    if isinstance(block, dict) and block.get("type") == "text"
                ]
                last_user = " ".join(parts)
            break

    with _lock:
        _state["last_user_text"] = last_user[:2000]

    return (
        "Acknowledged. Working on the fixture repository "
        "(ai-feature-flow/feature-flow-example).\n"
        f"Task received: {last_user[:200]}"
    )


def _reply_blocks(body: dict) -> list:
    """The content blocks of the reply.

    Normally a single text block. When /control/tool-call has armed one, the
    reply is a tool_use block instead, which is what makes the CLI run a real
    tool call and therefore fire PreToolUse and PostToolUse.
    """
    with _lock:
        armed = _state.get("tool_call")

    if not armed:
        return [{"type": "text", "text": _reply_text(body)}]

    with _lock:
        _state["tool_call"] = None
        command = _state.get("tool_command") or "git rev-parse --abbrev-ref HEAD"
        raw_input = _state.get("tool_input")

    if raw_input:
        try:
            return [{
                "type": "tool_use",
                "id": "toolu_mock_1",
                "name": armed,
                "input": json.loads(raw_input),
            }]
        except ValueError:
            pass

    return [{
        "type": "tool_use",
        "id": "toolu_mock_1",
        "name": armed,
        "input": {"command": command},
    }]


def _message_payload(body: dict, blocks: list) -> dict:
    return {
        "id": "msg_mock_%d" % _state["requests"],
        "type": "message",
        "role": "assistant",
        "model": body.get("model") or MODEL,
        "content": blocks,
        "stop_reason": "tool_use" if any(b.get("type") == "tool_use" for b in blocks) else "end_turn",
        "stop_sequence": None,
        "usage": {
            "input_tokens": 1200,
            "output_tokens": 96,
            "cache_creation_input_tokens": 0,
            "cache_read_input_tokens": 0,
        },
    }


def _sse(message: dict) -> bytes:
    blocks = message["content"]
    events = [
        ("message_start", {"type": "message_start", "message": {
            "id": message["id"], "type": "message", "role": "assistant",
            "model": message["model"], "content": [], "stop_reason": None,
            "stop_sequence": None,
            "usage": {"input_tokens": 1200, "output_tokens": 1},
        }}),
    ]
    for index, block in enumerate(blocks):
        if block.get("type") == "tool_use":
            events.append(("content_block_start", {
                "type": "content_block_start", "index": index,
                "content_block": {"type": "tool_use", "id": block["id"],
                                  "name": block["name"], "input": {}}}))
            events.append(("content_block_delta", {
                "type": "content_block_delta", "index": index,
                "delta": {"type": "input_json_delta",
                          "partial_json": json.dumps(block["input"])}}))
        else:
            events.append(("content_block_start", {
                "type": "content_block_start", "index": index,
                "content_block": {"type": "text", "text": ""}}))
            events.append(("content_block_delta", {
                "type": "content_block_delta", "index": index,
                "delta": {"type": "text_delta", "text": block.get("text", "")}}))
        events.append(("content_block_stop", {"type": "content_block_stop", "index": index}))

    events.append(("message_delta", {
        "type": "message_delta",
        "delta": {"stop_reason": message["stop_reason"], "stop_sequence": None},
        "usage": {"output_tokens": 96}}))
    events.append(("message_stop", {"type": "message_stop"}))

    out = []
    for name, payload in events:
        out.append("event: %s\n" % name)
        out.append("data: %s\n\n" % json.dumps(payload))
    return "".join(out).encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # keep the console readable
        sys.stderr.write("[mock-router] %s\n" % (fmt % args))

    def _send(self, code: int, payload: bytes, content_type: str) -> None:
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _json(self, code: int, obj: dict) -> None:
        self._send(code, json.dumps(obj).encode("utf-8"), "application/json")

    def _read_body(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if not length:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return {}

    def do_GET(self) -> None:
        path = self.path.split("?")[0]
        if path == "/healthz":
            self._json(200, {"ok": True, "model": MODEL,
                             "uptime_s": round(time.time() - _state["started"], 1)})
        elif path == "/control/stats":
            with _lock:
                self._json(200, dict(_state))
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self) -> None:
        path, _, query = self.path.partition("?")
        body = self._read_body()

        if path == "/control/delay":
            params = dict(p.split("=", 1) for p in query.split("&") if "=" in p)
            with _lock:
                _state["delay_ms"] = int(params.get("ms", 0))
                delay = _state["delay_ms"]
            self._json(200, {"delay_ms": delay})
            return

        if path == "/control/tool-call":
            params = dict(p.split("=", 1) for p in query.split("&") if "=" in p)
            with _lock:
                _state["tool_call"] = params.get("name")
                _state["tool_command"] = unquote(params.get("command") or "git rev-parse --abbrev-ref HEAD")
                _state["tool_input"] = unquote(params["input"]) if "input" in params else None
                armed = _state["tool_call"]
            self._json(200, {"armed": armed, "command": _state["tool_command"]})
            return

        if path == "/control/reset":
            with _lock:
                _state.update(requests=0, delay_ms=0, last_model=None,
                              last_user_text=None, tool_names=[], tool_call=None)
            self._json(200, {"ok": True})
            return

        if path == "/v1/messages/count_tokens":
            self._json(200, {"input_tokens": 1200})
            return

        if path != "/v1/messages":
            self._json(404, {"error": "not found"})
            return

        with _lock:
            _state["requests"] += 1
            _state["last_model"] = body.get("model")
            system = body.get("system")
            _state["last_system_chars"] = len(system) if isinstance(system, str) else (
                len(json.dumps(system)) if system else 0)
            tools = body.get("tools") or []
            _state["tool_names"] = [t.get("name") for t in tools if isinstance(t, dict)]
            delay = _state["delay_ms"]

        if delay:
            time.sleep(delay / 1000.0)

        message = _message_payload(body, _reply_blocks(body))

        if body.get("stream"):
            payload = _sse(message)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "keep-alive")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        else:
            self._json(200, message)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8791)
    parser.add_argument("--host", default="127.0.0.1")
    args = parser.parse_args()

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    sys.stderr.write("[mock-router] listening on http://%s:%d\n" % (args.host, args.port))
    sys.stderr.flush()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
