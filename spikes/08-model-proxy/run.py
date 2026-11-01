"""SP5 harness: one agent step behind the model proxy, then the checks of
docs/spikes/sp5-results.md section 3. Secrets are compared, never printed.

Usage: python3 run.py <trial> [--variant main|n1|n2] --work <dir>
"""
import argparse
import json
import os
import secrets
import shutil
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = 7801
CLOSED_PORT = 7899
CREDENTIAL_NAMES = ("CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY")
PROMPT = """Do exactly these steps and nothing else:
1. Use the Write tool to create notes.md containing three bullet points describing what a function add(a, b) should do.
2. Run the command `env` with the Bash tool.
3. Use the Write tool to save that command's full output, unchanged, to env.txt.
Then stop."""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("trial")
    parser.add_argument("--variant", default="main", choices=["main", "n1", "n2"])
    parser.add_argument("--work", required=True)
    args = parser.parse_args()

    credential = os.environ.get("CLAUDE_CODE_OAUTH_TOKEN")
    if not credential:
        sys.exit("CLAUDE_CODE_OAUTH_TOKEN is not set for the proxy; stopping rather than substituting anything")

    name = f"{args.variant}-{args.trial}"
    work = os.path.join(args.work, name)
    shutil.rmtree(work, ignore_errors=True)
    sandbox = os.path.join(work, "sandbox")
    config = os.path.join(work, "claude-config")
    os.makedirs(sandbox)
    os.makedirs(config)
    home = os.path.join(work, "home")
    os.makedirs(home)
    proxy_log = os.path.join(work, "proxy.jsonl")
    step_token = "sp5-step-" + secrets.token_hex(16)

    bundle = os.path.join(args.work, "system-roots.pem")
    if not os.path.exists(bundle):
        with open(bundle, "w") as f:
            subprocess.run(["security", "find-certificate", "-a", "-p", "/System/Library/Keychains/SystemRootCertificates.keychain",
                            "/Library/Keychains/System.keychain"], stdout=f, check=True)

    proxy = None
    if args.variant != "n1":
        proxy = subprocess.Popen([sys.executable, os.path.join(HERE, "proxy.py")], env={
            "PATH": os.environ["PATH"], "PORT": str(PORT), "STEP_TOKEN": step_token,
            "UPSTREAM_TOKEN": credential, "PROXY_LOG": proxy_log,
            "PROXY_MODE": "passthrough" if args.variant == "n2" else "normal", "CA_BUNDLE": bundle,
        })
        wait_for_port(PORT)

    # The agent's environment is built from scratch: no inherited credential.
    agent_env = {
        "PATH": os.environ["PATH"],
        # The sandbox's own home, so the runtime's shell reads no user startup files
        # (amendment after trial 1, which kept the user's home).
        "HOME": home if int(args.trial) >= 2 else os.environ["HOME"],
        "LANG": os.environ.get("LANG", "en_US.UTF-8"),
        "TMPDIR": os.environ.get("TMPDIR", "/tmp"),
        "CLAUDE_CONFIG_DIR": config,
        "ANTHROPIC_BASE_URL": f"http://127.0.0.1:{CLOSED_PORT if args.variant == 'n1' else PORT}",
        "ANTHROPIC_AUTH_TOKEN": step_token,
    }
    command = ["claude", "-p", PROMPT, "--model", "haiku", "--output-format", "stream-json", "--verbose",
               "--setting-sources", "project", "--strict-mcp-config",
               "--allowedTools", "Write", "Bash(env)"]
    started = time.time()
    with open(os.path.join(work, "transcript.jsonl"), "w") as out, open(os.path.join(work, "stderr.log"), "w") as err:
        try:
            exit_code = subprocess.run(command, cwd=sandbox, env=agent_env, stdout=out, stderr=err,
                                       timeout=300).returncode
        except subprocess.TimeoutExpired:
            exit_code = "timeout"
    elapsed = round(time.time() - started, 1)
    if proxy:
        proxy.terminate()
        proxy.wait()

    # ---- Evidence ----
    requests = read_jsonl(proxy_log)
    forwarded = [r for r in requests if r.get("step_token_ok")]
    model_requests = [r for r in forwarded if r["path"].startswith("/v1/messages") and "count_tokens" not in r["path"]]
    transcript = read_text(os.path.join(work, "transcript.jsonl"))
    assistant_ids = set()
    for line in transcript.splitlines():
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        if record.get("type") == "assistant":
            assistant_ids.add(record.get("message", {}).get("id"))
    notes = read_text(os.path.join(sandbox, "notes.md"))
    env_view = read_text(os.path.join(sandbox, "env.txt"))

    surfaced = []
    for root, _, files in os.walk(work):
        if os.path.join(work, "claude-config") in root:
            continue
        for file in files:
            if credential in read_text(os.path.join(root, file)):
                surfaced.append(os.path.relpath(os.path.join(root, file), work))

    checks = {
        "E proxy forwarded at least one model request, all with the step token":
            bool(model_requests) and all(r.get("step_token_ok") for r in requests if r["status"] != 401),
        "C1 step completed": bool(notes.strip()) and bool(env_view.strip()) and exit_code == 0,
        "C2 no credential in the agent's environment":
            not any(n in agent_env for n in CREDENTIAL_NAMES) and credential not in json.dumps(agent_env) and
            bool(env_view.strip()) and not any(n in env_view for n in CREDENTIAL_NAMES) and credential not in env_view,
        "C3 all model traffic through the proxy": len(model_requests) >= len(assistant_ids) > 0,
        "C4 credential never surfaced": not surfaced,
    }
    if args.variant != "main":
        checks = {k: v for k, v in checks.items() if k.startswith("C1")}

    summary = {
        "variant": args.variant, "trial": args.trial, "exit": exit_code, "elapsed_seconds": elapsed,
        "proxy_requests": len(requests), "model_requests": len(model_requests),
        "upstream_statuses": sorted({r["status"] for r in requests}),
        "assistant_messages": len(assistant_ids),
        "tokens": {"input": sum(r.get("input_tokens") or 0 for r in model_requests),
                   "output": sum(r.get("output_tokens") or 0 for r in model_requests)},
        "models": sorted({r.get("model") for r in model_requests if r.get("model")}),
        "env_txt_lines": len(env_view.splitlines()), "surfaced_in": surfaced,
        "checks": checks,
    }
    with open(os.path.join(work, "summary.json"), "w") as f:
        json.dump(summary, f, indent=2)
    evidence = os.path.join(HERE, "runs", name)
    shutil.rmtree(evidence, ignore_errors=True)
    shutil.copytree(work, evidence, ignore=shutil.ignore_patterns("claude-config"))

    flags = " ".join(("PASS " if ok else "FAIL ") + k.split()[0] for k, ok in checks.items())
    print(f"SP5 {name}: exit={exit_code} {elapsed}s proxy={len(requests)} model={len(model_requests)} "
          f"assistant={len(assistant_ids)} statuses={summary['upstream_statuses']} models={summary['models']} | {flags}")
    return 0 if all(checks.values()) else 1


def wait_for_port(port):
    for _ in range(50):
        with socket.socket() as s:
            if s.connect_ex(("127.0.0.1", port)) == 0:
                return
        time.sleep(0.1)
    raise RuntimeError("proxy did not start")


def read_jsonl(path):
    return [json.loads(line) for line in read_text(path).splitlines() if line.strip()]


def read_text(path):
    try:
        with open(path, errors="replace") as f:
            return f.read()
    except FileNotFoundError:
        return ""


if __name__ == "__main__":
    sys.exit(main())
