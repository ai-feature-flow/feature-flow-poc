"""SP4 harness: concurrent catch-up, re-test and lock-free merge against the fixture
repository on GitHub, then the checks of docs/spikes/sp4-results.md section 3.

Usage: python3 merge.py <S1|S2|S3> <run> [--variant main|n1|n2] --work <dir>
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

REMOTE = "git@github.com-sparkkz-nz:ai-feature-flow/feature-flow-example.git"
HERE = os.path.dirname(os.path.abspath(__file__))
SCENARIOS = {
    # tickets, allowance, forced rounds
    "S1": (3, 3, True),
    "S2": (5, 3, False),
    "S3": (4, 3, True),
}
FORCED_TEST_SECONDS = 1.0
DEADLINE_SECONDS = 600

log_lock = threading.Lock()
events = []


def log(ticket, event, **detail):
    record = {"t": round(time.time(), 3), "ticket": ticket, "event": event, **detail}
    with log_lock:
        events.append(record)
        print(json.dumps(record), flush=True)


# GitHub resets SSH connections under bursts of new ones. Connections are reused,
# and a connection failure is infrastructure: retried, never counted as an attempt.
SSH_CONTROL = "/tmp/sp4-%C"  # short: Unix socket paths are limited to about 104 bytes
GIT_ENV = {**os.environ, "GIT_SSH_COMMAND":
           f"ssh -o ControlMaster=auto -o ControlPath={SSH_CONTROL} -o ControlPersist=120"}
TRANSIENT = ("Connection reset", "kex_exchange_identification", "Connection closed", "timed out",
             "Could not resolve host", "unexpected disconnect", "early EOF")
NETWORK = ("fetch", "push", "clone", "ls-remote")


def git(cwd, *args, check=True, ticket=None):
    for attempt in range(6):
        result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, env=GIT_ENV)
        transient = args[0] in NETWORK and result.returncode != 0 and any(
            marker in result.stderr for marker in TRANSIENT)
        if not transient:
            break
        log(ticket, "infra_retry", command=" ".join(args[:2]), attempt=attempt + 1,
            error=result.stderr.strip().splitlines()[0] if result.stderr.strip() else "")
        time.sleep(1 + attempt * 2)
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def rev(cwd, ref):
    return git(cwd, "rev-parse", ref).stdout.strip()


class Rounds:
    """Releases every still-active ticket's merge push at the same moment."""

    def __init__(self, tickets):
        self.active = set(tickets)
        self.arrived = set()
        self.generation = 0
        self.condition = threading.Condition()

    def wait(self, ticket):
        with self.condition:
            generation = self.generation
            self.arrived.add(ticket)
            self._release_if_ready()
            while self.generation == generation:
                self.condition.wait()

    def leave(self, ticket):
        with self.condition:
            self.active.discard(ticket)
            self._release_if_ready()

    def _release_if_ready(self):
        if self.arrived and self.arrived >= self.active:
            self.arrived = set()
            self.generation += 1
            self.condition.notify_all()


def setup(work, prefix, tickets):
    seed = os.path.join(work, "seed")
    git(work, "clone", "-q", REMOTE, seed)
    for key, value in (("user.name", "sp4-harness"), ("user.email", "sp4-harness@example.invalid")):
        git(seed, "config", key, value)
    parent = f"{prefix}/parent"
    git(seed, "checkout", "-q", "-b", parent, "origin/main")
    git(seed, "push", "-q", "origin", f"HEAD:refs/heads/{parent}")
    setup_parent = rev(seed, "HEAD")
    for k in range(tickets):
        branch = f"{prefix}/ticket-{k}"
        git(seed, "checkout", "-q", "-b", branch, setup_parent)
        path = f"{prefix}/ticket-{k}.txt"
        os.makedirs(os.path.join(seed, os.path.dirname(path)), exist_ok=True)
        with open(os.path.join(seed, path), "w") as f:
            f.write(f"ticket {k} of {prefix}\n")
        git(seed, "add", path)
        git(seed, "commit", "-q", "-m", f"SP4 ticket {k}")
        git(seed, "push", "-q", "origin", f"HEAD:refs/heads/{branch}")
    clones = []
    for k in range(tickets):
        clone = os.path.join(work, f"worker-{k}")
        git(work, "clone", "-q", seed, clone)
        git(clone, "remote", "set-url", "origin", REMOTE)
        for key, value in (("user.name", "sp4-harness"), ("user.email", "sp4-harness@example.invalid")):
            git(clone, "config", key, value)
        clones.append(clone)
    return seed, setup_parent, clones


def run_ticket(k, clone, prefix, allowance, forced, rounds, variant, results):
    parent, branch = f"{prefix}/parent", f"{prefix}/ticket-{k}"
    state = {"merge_executions": 0, "last_tested": None, "recorded_parent": None, "outcome": None,
             "merge_commit": None, "merges": []}
    results[k] = state
    rng = random.Random(k * 7919 + int(time.time()))
    try:
        while True:
            # scm.catch_up
            git(clone, "fetch", "-q", "origin", f"+refs/heads/{parent}:refs/remotes/origin/{parent}",
                f"+refs/heads/{branch}:refs/remotes/origin/{branch}", ticket=k)
            git(clone, "checkout", "-q", "-B", branch, f"origin/{branch}")
            before = rev(clone, "HEAD")
            recorded = rev(clone, f"origin/{parent}")
            changed = git(clone, "merge-base", "--is-ancestor", recorded, before, check=False).returncode != 0
            if changed:
                git(clone, "merge", "-q", "--no-edit", f"origin/{parent}")
                git(clone, "push", "-q", "origin", f"HEAD:refs/heads/{branch}", ticket=k)
            state["recorded_parent"] = recorded
            log(k, "catch_up", changed=changed, recorded_parent=recorded, head=rev(clone, "HEAD"))

            # Re-test when the branch changed, and before the first merge.
            if state["last_tested"] is None or (changed and variant != "n2"):
                state["last_tested"] = rev(clone, "HEAD")
                seconds = FORCED_TEST_SECONDS if forced else rng.uniform(2, 8)
                log(k, "test", tested_head=state["last_tested"], seconds=round(seconds, 1))
                time.sleep(seconds)

            # scm.merge: the allowance is checked before every execution, including re-entry.
            if state["merge_executions"] >= allowance:
                state["outcome"] = "attempts_exhausted"
                log(k, "attempts_exhausted", merge_executions=state["merge_executions"])
                return
            if forced:
                rounds.wait(k)
            state["merge_executions"] += 1
            head = rev(clone, "HEAD")
            tree = rev(clone, "HEAD^{tree}")
            commit = git(clone, "commit-tree", tree, "-p", recorded, "-p", head,
                         "-m", f"SP4 merge ticket {k}").stdout.strip()
            if variant == "n1":
                push = git(clone, "push", "--porcelain", "--force", "origin",
                           f"{commit}:refs/heads/{parent}", check=False, ticket=k)
            else:
                push = git(clone, "push", "--porcelain", f"--force-with-lease=refs/heads/{parent}:{recorded}",
                           "origin", f"{commit}:refs/heads/{parent}", check=False, ticket=k)
            output = (push.stdout + push.stderr).strip()
            if push.returncode == 0:
                state["outcome"] = "merged"
                state["merge_commit"] = commit
                state["merges"].append({"commit": commit, "recorded_parent": recorded, "head": head,
                                        "tested": state["last_tested"]})
                log(k, "merge", result="merged", execution=state["merge_executions"], commit=commit,
                    recorded_parent=recorded, head=head, tested=state["last_tested"])
                return
            if "rejected" in output or "stale info" in output or "fetch first" in output:
                # Idempotency (docs/04 section 7.3): if the parent already holds this merge, it happened.
                git(clone, "fetch", "-q", "origin", f"+refs/heads/{parent}:refs/remotes/origin/{parent}", ticket=k)
                if git(clone, "merge-base", "--is-ancestor", commit, f"origin/{parent}", check=False).returncode == 0:
                    state["outcome"] = "merged"
                    state["merge_commit"] = commit
                    log(k, "merge", result="merged", idempotent=True, execution=state["merge_executions"],
                        commit=commit, recorded_parent=recorded, head=head, tested=state["last_tested"])
                    return
                log(k, "merge", result="base_moved", execution=state["merge_executions"],
                    recorded_parent=recorded, detail=[line for line in output.splitlines() if line.startswith("!")])
                continue
            state["outcome"] = "error"
            log(k, "merge", result="error", detail=output)
            return
    finally:
        if forced:
            rounds.leave(k)


def verify(work, seed, prefix, setup_parent, results, allowance, scenario):
    parent = f"{prefix}/parent"
    git(seed, "fetch", "-q", "origin", f"+refs/heads/{parent}:refs/remotes/origin/{parent}")
    final = rev(seed, f"origin/{parent}")
    chain = git(seed, "rev-list", "--first-parent", f"{setup_parent}..{final}").stdout.split()
    merged = {k: s for k, s in results.items() if s["outcome"] == "merged"}
    engine_commits = {s["merge_commit"] for s in merged.values()}

    lost = [k for k, s in merged.items()
            if git(seed, "merge-base", "--is-ancestor", s["last_tested"], final, check=False).returncode != 0]
    c1 = not lost and set(chain) == engine_commits and len(chain) == len(merged)

    # Only merges that landed can have landed untested. An overwritten merge commit
    # is unreachable on the remote, so it is counted under C1, not here.
    untested = []
    for k, s in merged.items():
        if s["merge_commit"] not in chain:
            continue
        parents = git(seed, "show", "-s", "--format=%P", s["merge_commit"]).stdout.split()
        if parents != [s["recorded_parent"], s["last_tested"]]:
            untested.append(k)
    c2 = not untested

    malformed = []
    for k in results:
        sequence = [e for e in events if e["ticket"] == k]
        for i, e in enumerate(sequence):
            if e["event"] == "merge" and e.get("result") == "base_moved":
                following = [x["event"] for x in sequence[i + 1:i + 4]]
                catch = sequence[i + 1] if i + 1 < len(sequence) else None
                if not catch or catch["event"] != "catch_up":
                    malformed.append(f"{k}: no catch_up after base_moved")
                elif catch["changed"] and (i + 2 >= len(sequence) or sequence[i + 2]["event"] != "test"):
                    malformed.append(f"{k}: changed catch_up not followed by test ({following})")
        if results[k]["merge_executions"] > allowance:
            malformed.append(f"{k}: {results[k]['merge_executions']} merge executions")
    c3 = not malformed

    outcomes = {k: s["outcome"] for k, s in results.items()}
    refusals = sum(1 for e in events if e["event"] == "merge" and e.get("result") == "base_moved")
    if scenario == "S1":
        c4 = all(o == "merged" for o in outcomes.values())
    elif scenario == "S2":
        c4 = all(o in ("merged", "attempts_exhausted") for o in outcomes.values())
    else:
        exhausted = [k for k, o in outcomes.items() if o == "attempts_exhausted"]
        absent = all(git(seed, "cat-file", "-e", f"{final}:{prefix}/ticket-{k}.txt", check=False).returncode != 0
                     for k in exhausted)
        c4 = (sum(o == "merged" for o in outcomes.values()) == 3 and len(exhausted) == 1 and
              results[exhausted[0]]["merge_executions"] == 3 and absent)
    expected_refusals = {"S1": 3, "S3": 6}.get(scenario)
    return {
        "final_parent": final, "first_parent_chain": chain, "outcomes": outcomes, "refusals": refusals,
        "lost": lost, "untested": untested, "malformed": malformed,
        "checks": {
            "E1 base_moved refusals" + (f" == {expected_refusals}" if expected_refusals else " >= 1 (across S2 runs)"):
                refusals == expected_refusals if expected_refusals else refusals >= 1,
            "C1 no lost merge": c1,
            "C2 nothing untested lands": c2,
            "C3 loops well formed; allowance respected": c3,
            "C4 termination as expected": c4,
        },
    }


def cleanup(seed, prefix, tickets, final):
    tag = f"{prefix}/final"
    git(seed, "push", "-q", "origin", f"{final}:refs/tags/{tag}", check=False)
    refs = [f":refs/heads/{prefix}/parent"] + [f":refs/heads/{prefix}/ticket-{k}" for k in range(tickets)]
    git(seed, "push", "-q", "origin", *refs, check=False)
    return tag


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("scenario", choices=list(SCENARIOS))
    parser.add_argument("run")
    parser.add_argument("--variant", default="main", choices=["main", "n1", "n2"])
    parser.add_argument("--work", required=True)
    args = parser.parse_args()

    tickets, allowance, forced = SCENARIOS[args.scenario]
    name = f"{args.scenario}-{args.variant}-{args.run}"
    prefix = f"sp4/{name}-{int(time.time())}"
    work = os.path.join(args.work, name)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)

    seed, setup_parent, clones = setup(work, prefix, tickets)
    log(None, "setup", prefix=prefix, setup_parent=setup_parent, tickets=tickets, allowance=allowance, forced=forced)
    rounds = Rounds(range(tickets))
    results = {}
    threads = [threading.Thread(target=run_ticket, daemon=True,
                                args=(k, clones[k], prefix, allowance, forced, rounds, args.variant, results))
               for k in range(tickets)]
    started = time.time()
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(max(1, DEADLINE_SECONDS - (time.time() - started)))
    elapsed = round(time.time() - started, 1)
    timed_out = any(thread.is_alive() for thread in threads)

    verdict = verify(work, seed, prefix, setup_parent, results, allowance, args.scenario)
    if timed_out:
        verdict["checks"]["C4 termination as expected"] = False
    tag = cleanup(seed, prefix, tickets, verdict["final_parent"])

    summary = {"scenario": args.scenario, "variant": args.variant, "run": args.run, "prefix": prefix,
               "evidence_tag": tag, "elapsed_seconds": elapsed, "timed_out": timed_out,
               "merge_executions": {k: s["merge_executions"] for k, s in results.items()}, **verdict}
    out = os.path.join(HERE, "runs", name)
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "summary.json"), "w") as f:
        json.dump(summary, f, indent=2)
    with open(os.path.join(out, "events.jsonl"), "w") as f:
        f.writelines(json.dumps(e) + "\n" for e in events)

    flags = " ".join(("PASS " if ok else "FAIL ") + k.split()[0] for k, ok in summary["checks"].items())
    print(f"SP4 {name}: {elapsed}s outcomes={summary['outcomes']} refusals={summary['refusals']} "
          f"executions={summary['merge_executions']} | {flags}", file=sys.stderr)
    return 0 if all(summary["checks"].values()) else 1


if __name__ == "__main__":
    sys.exit(main())
