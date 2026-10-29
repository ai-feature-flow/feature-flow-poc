"""Push the fixture branch using a credential minted by the broker.

Review 14 A3. The engine never holds a long-lived credential: it asks the
credential broker for a short-lived, single-repository token and uses it for
one command. The key file lives outside the repository and is named by
reference in the secrets store (floor F2).

Usage: push-branch.py <branch> [--base <branch>]
"""

import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SECRETS = r"D:\Users\Stuart\keys\feature-flow-secrets.json"
FIXTURE = os.path.join(ROOT, "spikes", "02-real-run", "fixture")
TOKEN_TOOL = os.path.join(ROOT, "tools", "gh_app_token.py")


def load_secrets():
    with open(SECRETS, "r", encoding="utf-8") as handle:
        return json.load(handle)


def resolve(store, key):
    raw = store[key]
    if os.path.isabs(raw) and os.path.isfile(raw):
        with open(raw, "r", encoding="utf-8") as handle:
            return handle.read().strip()
    return raw


def mint_token(store):
    """Mint an installation token through the broker's own tooling.

    The key is passed as a path, not a value: the secrets store entry names a
    file outside the repository, and the value is read by the tool that needs
    it. Nothing here ever holds the key text.
    """
    result = subprocess.run(
        [
            sys.executable,
            TOKEN_TOOL,
            "--app-id", store["github-app-id"],
            "--key", store["github-app-key"],
            "--installation", store["github-installation-id"],
            "--emit",
        ],
        capture_output=True,
        text=True,
        check=True,
    )
    return result.stdout.strip()


def git(*args, env=None):
    result = subprocess.run(
        ["git", *args], cwd=FIXTURE, capture_output=True, text=True, env=env
    )
    return result.returncode, result.stdout.strip(), result.stderr.strip()


def main():
    branch = sys.argv[1] if len(sys.argv) > 1 else "ff/ticket/spike-2-1"
    base = "ff/epic/spike-2"
    if "--base" in sys.argv:
        base = sys.argv[sys.argv.index("--base") + 1]

    store = load_secrets()
    token = mint_token(store)

    # The token goes into the remote URL for one command only. It is never
    # written to the repository's config or to a file (floor F2).
    remote = (
        f"https://x-access-token:{token}@github.com/"
        "ai-feature-flow/feature-flow-example.git"
    )

    code, out, err = git("rev-parse", "--abbrev-ref", "HEAD")
    current = out
    print(f"current branch: {current}")

    if current != branch:
        code, out, err = git("checkout", "-B", branch, base)
        print(f"checkout -B {branch} {base}: exit={code} {err}")

    code, out, err = git("push", remote, branch)
    # The token is used for this command only; it is not persisted anywhere.
    print(f"push {branch}: exit={code}")
    if out:
        print(out)
    if err:
        print(err)

    return code


if __name__ == "__main__":
    raise SystemExit(main())
