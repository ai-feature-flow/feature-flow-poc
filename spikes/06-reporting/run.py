"""SP3 harness: one trial of one task on one runtime, then the checks of
docs/spikes/sp3-results.md section 3.

Usage: python3 run.py <claude|copilot> <T1|T2|T3|T4|N1|N2> <trial> [--work <dir>]

The sandbox is created under --work (outside this repository, so neither
runtime loads the repository's instructions); the evidence is copied to
runs/<runtime>-<task>-<trial>/.
"""
import argparse
import json
import os
import random
import shutil
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))

PREAMBLE = """You are running as one step of an automated engine. When you finish, you must report your result by calling the engine's `report` tool, with:
- outcome: one of "implemented" (you did the task) or "blocked" (you could not).
- summary: a short plain-text summary, at most 2 KB.
- outputs: an object with "files_changed" (an integer: how many files you created or changed) and "risk" (one of "low", "medium", "high").
- artifacts: a list of the file paths you produced, relative to the working directory.
If the engine rejects your report, read its message, fix the report, and call `report` again. When the engine says the report is accepted, or that the attempt has failed, stop."""

FALLBACK = """If the `report` tool is not available to you, write the same report as a JSON object with the keys outcome, summary, outputs and artifacts to the file out/report.json instead."""

TASK = "Task: write the file notes.md containing three bullet points describing what a function add(a, b) should do. Do nothing else."

TASK_ASK = """Task: first call the ask_human tool once with the question "Which label should notes.md use?". Wait for the answer; it may take a while. Then write notes.md with that label on its first line, followed by three bullet points describing what a function add(a, b) should do. In your report, add the key "answer" to outputs, set to the label exactly as given. Do nothing else."""

# task: (uses MCP, tools mode, prompt parts)
TASKS = {
    "T1": (True, "normal", [PREAMBLE, TASK]),
    "T2": (True, "inject_first", [PREAMBLE, TASK]),
    "T3": (False, "normal", [PREAMBLE, FALLBACK, TASK]),
    "T3a": (False, "normal", [PREAMBLE, FALLBACK, TASK]),
    "T4": (True, "normal", [PREAMBLE, TASK_ASK]),
    "N1": (False, "normal", [PREAMBLE, TASK]),
    "N2": (True, "reject_all", [PREAMBLE, TASK]),
}
TIMEOUT_SECONDS = 300


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("runtime", choices=["claude", "copilot"])
    parser.add_argument("task", choices=list(TASKS))
    parser.add_argument("trial")
    parser.add_argument("--work", required=True)
    args = parser.parse_args()

    name = f"{args.runtime}-{args.task}-{args.trial}"
    work = os.path.join(args.work, name)
    shutil.rmtree(work, ignore_errors=True)
    sandbox = os.path.join(work, "sandbox")
    os.makedirs(sandbox)
    subprocess.run(["git", "init", "-q", sandbox], check=True)
    if args.task == "T3a":
        # The engine prepares the step's output folder (amendment in section 3).
        os.makedirs(os.path.join(sandbox, "out"))
    tool_log = os.path.join(work, "tools.jsonl")
    hook_log = os.path.join(work, "hook.jsonl")
    question_file = os.path.join(work, "question.txt")
    answer_file = os.path.join(work, "answer.txt")
    uses_mcp, mode, parts = TASKS[args.task]
    prompt = "\n\n".join(parts)
    nonce = f"{random.choice(['teal', 'amber', 'violet'])}-{random.randint(10000, 99999)}"

    server = {
        "command": "python3",
        "args": [os.path.join(HERE, "engine_tools.py")],
        "env": {"TOOL_LOG": tool_log, "SANDBOX": sandbox, "TOOLS_MODE": mode,
                "QUESTION_FILE": question_file, "ANSWER_FILE": answer_file},
    }
    env = dict(os.environ)
    if args.runtime == "claude":
        command = ["claude", "-p", prompt, "--model", "haiku", "--output-format", "stream-json", "--verbose",
                   "--setting-sources", "project", "--strict-mcp-config",
                   "--allowedTools", "Write", "mcp__engine__report", "mcp__engine__ask_human"]
        if uses_mcp:
            config_path = os.path.join(work, "mcp.json")
            with open(config_path, "w") as f:
                json.dump({"mcpServers": {"engine": server}}, f)
            command[command.index("--strict-mcp-config"):command.index("--strict-mcp-config")] = ["--mcp-config", config_path]
        env["MCP_TOOL_TIMEOUT"] = "600000"
    else:
        hooks_dir = os.path.join(sandbox, ".github", "hooks")
        os.makedirs(hooks_dir)
        with open(os.path.join(hooks_dir, "sp3.json"), "w") as f:
            json.dump({"hooks": {"PreToolUse": [{
                "type": "command", "timeoutSec": 10,
                "command": f"HOOK_LOG={hook_log} python3 {os.path.join(HERE, 'hook_log.py')}",
            }]}}, f)
        # The same narrow approvals as the Claude Code trials: file writes and the
        # engine's tools; no shell.
        command = ["copilot", "-p", prompt, "--output-format", "json", "--trust-folder",
                   "--allow-tool", "write", "--allow-tool", "engine", "--deny-tool", "shell",
                   "--disable-builtin-mcps", "--no-custom-instructions", "--no-ask-user", "--no-auto-update",
                   "--log-dir", os.path.join(work, "copilot-logs")]
        # A separate home keeps the user's own hooks, agents and plugins out of the
        # trial; the sign-in is kept in the keychain, so it still applies.
        env["COPILOT_HOME"] = os.path.join(args.work, "copilot-home")
        if os.environ.get("SP3_COPILOT_MODEL"):
            command += ["--model", os.environ["SP3_COPILOT_MODEL"]]
        if uses_mcp:
            command += ["--additional-mcp-config", json.dumps(
                {"mcpServers": {"engine": {"type": "local", "tools": ["*"], **server}}})]

    started = time.time()
    question_opened_at = None

    def answer_when_asked():
        nonlocal question_opened_at
        while time.time() - started < TIMEOUT_SECONDS:
            if os.path.exists(question_file):
                question_opened_at = time.time()
                time.sleep(30)
                with open(answer_file + ".tmp", "w") as f:
                    f.write(nonce)
                os.replace(answer_file + ".tmp", answer_file)
                return
            time.sleep(0.2)

    if args.task == "T4":
        threading.Thread(target=answer_when_asked, daemon=True).start()

    with open(os.path.join(work, "stdout.jsonl"), "w") as out, open(os.path.join(work, "stderr.log"), "w") as err:
        try:
            exit_code = subprocess.run(command, cwd=sandbox, env=env, stdout=out, stderr=err,
                                       timeout=TIMEOUT_SECONDS).returncode
        except subprocess.TimeoutExpired:
            exit_code = "timeout"
    elapsed = round(time.time() - started, 1)

    # ---- Evidence ----
    events = read_jsonl(tool_log)
    reports = [e for e in events if e["event"] == "report"]
    asks = [e for e in events if e["event"] == "ask"]
    answers = [e for e in events if e["event"] == "answer_returned"]
    last = reports[-1] if reports else None
    notes = read_text(os.path.join(sandbox, "notes.md"))
    fallback = read_text(os.path.join(sandbox, "out", "report.json"))
    hook_calls = len(read_jsonl(hook_log))

    checks = {}
    if args.task in ("T1", "N1"):
        checks["C1 reported; last report valid"] = bool(last) and last["verdict"] == "accepted"
    if args.task in ("T2", "N2"):
        checks["E injected rejection on the first call"] = bool(reports) and reports[0]["verdict"] in ("rejected",)
        accepted = [r for r in reports if r["verdict"] == "accepted"]
        checks["C2 corrected within the turns; nothing after acceptance"] = (
            len(accepted) == 1 and accepted[0]["call"] <= 1 + 2 and
            accepted[0]["arguments"].get("summary", "").startswith("FF-142: ") and
            reports[-1]["verdict"] == "accepted")
    if args.task in ("T3", "T3a"):
        checks["C3 out/report.json valid against the contract"] = fallback_valid(fallback, sandbox)
    if args.task == "T4":
        open_seconds = None if not (asks and answers) else round(answers[0]["t"] - asks[0]["t"], 1)
        checks["E question open for at least 30 s"] = open_seconds is not None and open_seconds >= 30
        checks["C4 one ask; nonce in notes.md and outputs.answer; last report valid"] = (
            len(asks) == 1 and notes is not None and nonce in notes and bool(last) and
            last["verdict"] == "accepted" and last["arguments"].get("outputs", {}).get("answer") == nonce)
    if args.task.startswith("T"):
        checks["C5 exited 0 within 5 minutes"] = exit_code == 0

    summary = {
        "runtime": args.runtime, "task": args.task, "trial": args.trial, "exit": exit_code,
        "elapsed_seconds": elapsed, "nonce": nonce if args.task == "T4" else None,
        "report_verdicts": [r["verdict"] for r in reports], "asks": len(asks),
        "stopped_after_failure": None if args.task != "N2" else len(reports) <= 3,
        "notes_exists": notes is not None, "fallback_file": fallback is not None,
        "hook_calls": hook_calls if args.runtime == "copilot" else None,
        "model": detect_model(os.path.join(work, "stdout.jsonl"), args.runtime),
        "checks": checks,
    }
    with open(os.path.join(work, "summary.json"), "w") as f:
        json.dump(summary, f, indent=2)

    evidence = os.path.join(HERE, "runs", name)
    shutil.rmtree(evidence, ignore_errors=True)
    shutil.copytree(work, evidence, ignore=shutil.ignore_patterns(".git"))

    flags = " ".join(("PASS " if ok else "FAIL ") + k.split()[0] for k, ok in checks.items())
    print(f"SP3 {name}: exit={exit_code} {elapsed}s reports={summary['report_verdicts']} "
          f"asks={len(asks)} hooks={summary['hook_calls']} model={summary['model']} | {flags}")
    return 0 if all(checks.values()) else 1


def fallback_valid(text, sandbox):
    if text is None:
        return False
    try:
        report = json.loads(text)
    except json.JSONDecodeError:
        return False
    os.environ["SANDBOX"] = sandbox
    sys.path.insert(0, HERE)
    os.environ.setdefault("TOOL_LOG", os.devnull)
    import engine_tools
    engine_tools.SANDBOX = sandbox
    return isinstance(report, dict) and not engine_tools.contract_errors(report)


def detect_model(path, runtime):
    for line in read_text(path, "").splitlines():
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
        if runtime == "claude" and record.get("type") == "system" and record.get("model"):
            return record["model"]
        if runtime == "copilot":
            text = json.dumps(record)
            for key in ("model", "selectedModel"):
                value = find_key(record, key)
                if isinstance(value, str) and value:
                    return value
    return None


def find_key(node, key):
    if isinstance(node, dict):
        if key in node:
            return node[key]
        for value in node.values():
            found = find_key(value, key)
            if found is not None:
                return found
    elif isinstance(node, list):
        for value in node:
            found = find_key(value, key)
            if found is not None:
                return found
    return None


def read_jsonl(path):
    if not os.path.exists(path):
        return []
    with open(path) as f:
        return [json.loads(line) for line in f if line.strip()]


def read_text(path, default=None):
    if not os.path.exists(path):
        return default
    with open(path) as f:
        return f.read()


if __name__ == "__main__":
    sys.exit(main())
