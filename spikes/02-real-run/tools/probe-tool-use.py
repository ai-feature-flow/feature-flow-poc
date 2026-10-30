"""Probe structured tool use through a router and through local Ollama.

Spike 2, review 14 A3. The question: does the model emit a structured
tool_calls block, or does it describe the tool call as prose text?

The key is read from the secrets store by reference only (floor F2): the
store entry names a file outside the repository, and the value never
appears in this script, in the repository, or in the output.
"""

import json
import os
import sys
import urllib.error
import urllib.request

SECRETS = r"D:\Users\Stuart\keys\feature-flow-secrets.json"

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "write_file",
            "description": "Write content to a file at a path.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "The file path."},
                    "content": {"type": "string", "description": "The file content."},
                },
                "required": ["path", "content"],
            },
        },
    }
]

MESSAGES = [
    {
        "role": "user",
        "content": "Create a file at notes/hello.txt containing exactly the text: hello from the agent",
    }
]


def load_secrets():
    with open(SECRETS, "r", encoding="utf-8") as handle:
        return json.load(handle)


def resolve(store, key):
    """Resolve a secret the way LocalFileSecretsProvider does: a value that
    names an existing absolute file is that file's content."""
    raw = store[key]
    if os.path.isabs(raw) and os.path.isfile(raw):
        with open(raw, "r", encoding="utf-8") as handle:
            return handle.read().strip()
    return raw


def probe(label, endpoint, model, key):
    url = endpoint.rstrip("/") + "/chat/completions"
    body = json.dumps(
        {"model": model, "messages": MESSAGES, "tools": TOOLS, "tool_choice": "auto"}
    ).encode("utf-8")

    request = urllib.request.Request(
        url,
        data=body,
        headers={
            "Content-Type": "application/json",
            "Authorization": "Bearer " + key,
        },
        method="POST",
    )

    print("=" * 72)
    print(f"{label}")
    print(f"  endpoint: {endpoint}")
    print(f"  model:    {model}")
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", "replace")[:400]
        print(f"  HTTP {error.code}: {detail}")
        return {"label": label, "model": model, "structured": False, "error": f"HTTP {error.code}"}
    except Exception as error:  # noqa: BLE001 - a probe reports, it does not classify
        print(f"  transport error: {type(error).__name__}: {error}")
        return {"label": label, "model": model, "structured": False, "error": type(error).__name__}

    choice = (payload.get("choices") or [{}])[0]
    message = choice.get("message", {})
    finish = choice.get("finish_reason")
    calls = message.get("tool_calls") or []
    content = message.get("content") or ""

    print(f"  finish_reason: {finish}")
    print(f"  tool_calls:    {len(calls)}")
    for call in calls:
        name = call.get("function", {}).get("name")
        args = call.get("function", {}).get("arguments")
        print(f"    name={name} args={args}")
    if content:
        print(f"  content:       {content[:300]!r}")

    structured = finish == "tool_calls" and len(calls) > 0
    print(f"  STRUCTURED TOOL USE: {structured}")
    return {
        "label": label,
        "model": model,
        "endpoint": endpoint,
        "finish_reason": finish,
        "tool_call_count": len(calls),
        "tool_names": [c.get("function", {}).get("name") for c in calls],
        "content_excerpt": content[:300],
        "structured": structured,
    }


def main():
    store = load_secrets()
    results = []

    which = sys.argv[1] if len(sys.argv) > 1 else "all"

    if which in ("all", "router"):
        key = resolve(store, "openrouter-key-file")
        results.append(
            probe(
                "OpenRouter (the router the spike runs on)",
                store["model-router-endpoint"],
                store["model-default"],
                key,
            )
        )

    if which in ("all", "ollama"):
        for model in ["qwen2.5-coder:7b", "qwen2.5-coder:32b", "llama3.1:8b", "llama3:latest", "qwen2.5-coder:3b"]:
            results.append(probe(f"Ollama local", "http://localhost:11434/v1", model, "ollama"))

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "results", "tool-use-probe.json")
    with open(out, "w", encoding="utf-8") as handle:
        json.dump(results, handle, indent=2)
    print()
    print(f"wrote {os.path.abspath(out)}")


if __name__ == "__main__":
    main()
