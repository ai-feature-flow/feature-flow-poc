"""Verify a branch ruleset on the fixture repository with real API calls.

Review 14 A2: the code host must refuse a push or merge to `main` with the
engine's (App) token and with an agent's token, while leaving `ff/ticket/*`
branches usable for push and pull request.

Every step prints the HTTP status and the response body it got. Nothing is
asserted in code: the output is the evidence.

Usage:
    python tools/gh_ruleset_verify.py --app-id ID --key PATH --installation ID \
        --repo owner/name [--cleanup]
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

import jwt

API = "https://api.github.com"
AGENT_PERMISSIONS = {
    "contents": "write",
    "pull_requests": "write",
    "metadata": "read",
}


def mint_jwt(app_id: str, key_path: str) -> str:
    with open(key_path, "r", encoding="utf-8") as handle:
        private_key = handle.read()
    now = int(time.time())
    return jwt.encode(
        {"iat": now - 60, "exp": now + 540, "iss": app_id},
        private_key,
        algorithm="RS256",
    )


def call(method: str, path: str, token: str, payload: object = None) -> tuple:
    """Returns (status, body). Never raises on an HTTP error status."""
    url = path if path.startswith("http") else f"{API}{path}"
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "feature-flow-spike2",
            "Content-Type": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def show(label: str, status: int, body: str, limit: int = 900) -> None:
    print(f"\n--- {label}")
    print(f"HTTP {status}")
    if body.strip():
        print(body[:limit])


def mint(app_id: str, key_path: str, installation: str, scoped: bool, repo: str) -> tuple:
    """Mints a token. `scoped` narrows it to one repository and the agent grant."""
    payload = {}
    if scoped:
        payload = {
            "repositories": [repo.split("/")[1]],
            "permissions": AGENT_PERMISSIONS,
        }
    status, body = call(
        "POST", f"/app/installations/{installation}/access_tokens",
        mint_jwt(app_id, key_path), payload)
    if status != 201:
        print(f"minting the token failed: HTTP {status}\n{body}", file=sys.stderr)
        raise SystemExit(1)
    data = json.loads(body)
    return data["token"], data.get("permissions", {})


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--installation", required=True)
    parser.add_argument("--repo", default="ai-feature-flow/feature-flow-example")
    parser.add_argument("--branch", default="ff/ticket/a2-verify")
    parser.add_argument("--cleanup", action="store_true")
    args = parser.parse_args()

    repo = args.repo
    stamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    app_token, app_permissions = mint(args.app_id, args.key, args.installation, False, repo)
    agent_token, agent_permissions = mint(args.app_id, args.key, args.installation, True, repo)

    print("=== tokens")
    print(f"app token   permissions: {json.dumps(app_permissions, sort_keys=True)}")
    print(f"agent token permissions: {json.dumps(agent_permissions, sort_keys=True)}")

    show("rulesets on the repository (app token)",
         *call("GET", f"/repos/{repo}/rulesets", app_token))

    # ---------------------------------------------------------------- case 1
    print("\n=== case 1: app token pushes a commit straight to main")
    status, body = call(
        "PUT", f"/repos/{repo}/contents/a2-probe-app.txt", app_token,
        {
            "message": f"A2 probe: app token direct push to main ({stamp})",
            "content": stamp.encode("utf-8").hex(),
            "branch": "main",
        })
    show("app token PUT contents on branch=main", status, body)

    # ---------------------------------------------------------------- case 2
    print("\n=== case 2: agent token pushes a commit straight to main")
    status, body = call(
        "PUT", f"/repos/{repo}/contents/a2-probe-agent.txt", agent_token,
        {
            "message": f"A2 probe: agent token direct push to main ({stamp})",
            "content": stamp.encode("utf-8").hex(),
            "branch": "main",
        })
    show("agent token PUT contents on branch=main", status, body)

    # ---------------------------------------------------------------- case 3
    print(f"\n=== case 3: agent token pushes to {args.branch} and opens a pull request")
    status, body = call("GET", f"/repos/{repo}/git/ref/heads/main", app_token)
    show("read main", status, body, limit=300)
    main_sha = json.loads(body)["object"]["sha"]

    status, body = call(
        "POST", f"/repos/{repo}/git/refs", agent_token,
        {"ref": f"refs/heads/{args.branch}", "sha": main_sha})
    show(f"agent token creates branch {args.branch}", status, body, limit=400)

    status, body = call(
        "PUT", f"/repos/{repo}/contents/a2-probe-ticket.txt", agent_token,
        {
            "message": f"A2 probe: agent token push to {args.branch} ({stamp})",
            "content": stamp.encode("utf-8").hex(),
            "branch": args.branch,
        })
    show(f"agent token PUT contents on branch={args.branch}", status, body, limit=400)

    status, body = call(
        "POST", f"/repos/{repo}/pulls", app_token,
        {
            "title": "A2 probe: ticket branch still works",
            "head": args.branch,
            "base": "main",
            "body": "Opened by the review 14 A2 verification. Closed by the same script.",
        })
    show("app token opens a pull request from the ticket branch", status, body, limit=500)
    pr_number = json.loads(body).get("number") if status == 201 else None

    # ------------------------------------------------- case 1b: merge to main
    if pr_number is not None:
        print("\n=== case 1b: app token merges that pull request into main")
        status, body = call(
            "PUT", f"/repos/{repo}/pulls/{pr_number}/merge", app_token,
            {"merge_method": "squash", "commit_title": "A2 probe: app token merge"})
        show(f"app token PUT pulls/{pr_number}/merge", status, body)

        print("\n=== case 2b: agent token merges that pull request into main")
        status, body = call(
            "PUT", f"/repos/{repo}/pulls/{pr_number}/merge", agent_token,
            {"merge_method": "squash", "commit_title": "A2 probe: agent token merge"})
        show(f"agent token PUT pulls/{pr_number}/merge", status, body)

    if args.cleanup and pr_number is not None:
        print("\n=== cleanup")
        show(f"close PR #{pr_number}",
             *call("PATCH", f"/repos/{repo}/pulls/{pr_number}", app_token, {"state": "closed"}),
             limit=200)
        show(f"delete branch {args.branch}",
             *call("DELETE", f"/repos/{repo}/git/refs/heads/{args.branch}", app_token),
             limit=200)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
