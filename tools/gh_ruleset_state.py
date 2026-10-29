"""Supplementary evidence for review 14 A2.

Prints, with real API output:
  - the ruleset as the remote stores it, including whether the app can bypass it
  - the effective branch protection on `main`
  - the merge state of an open pull request into `main`
  - whether the app's own credential can edit the ruleset (a PATCH that changes
    nothing, so the probe cannot weaken the protection it is measuring)

Usage:
    python tools/gh_ruleset_state.py --app-id ID --key PATH --installation ID \
        --repo owner/name --ruleset ID [--pr N]
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

import jwt

API = "https://api.github.com"


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


def show(label: str, status: int, body: str, limit: int = 1400) -> None:
    print(f"\n--- {label}")
    print(f"HTTP {status}")
    if body.strip():
        print(body[:limit])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--installation", required=True)
    parser.add_argument("--repo", default="ai-feature-flow/feature-flow-example")
    parser.add_argument("--ruleset", required=True)
    parser.add_argument("--pr", type=int, default=None)
    args = parser.parse_args()

    repo = args.repo
    status, body = call(
        "POST", f"/app/installations/{args.installation}/access_tokens",
        mint_jwt(args.app_id, args.key))
    if status != 201:
        print(f"minting the token failed: HTTP {status}\n{body}", file=sys.stderr)
        return 1
    token = json.loads(body)["token"]

    show(f"GET /repos/{repo}/rulesets/{args.ruleset}",
         *call("GET", f"/repos/{repo}/rulesets/{args.ruleset}", token))

    show(f"GET /repos/{repo}/branches/main/protection",
         *call("GET", f"/repos/{repo}/branches/main/protection", token))

    if args.pr is not None:
        show(f"GET /repos/{repo}/pulls/{args.pr} (merge state)",
             *call("GET", f"/repos/{repo}/pulls/{args.pr}", token), limit=200)
        status, body = call("GET", f"/repos/{repo}/pulls/{args.pr}", token)
        if status == 200:
            pr = json.loads(body)
            print(f"\n  number:          {pr.get('number')}")
            print(f"  state:           {pr.get('state')}")
            print(f"  mergeable:       {pr.get('mergeable')}")
            print(f"  mergeable_state: {pr.get('mergeable_state')}")
            print(f"  merged:          {pr.get('merged')}")

    # A no-op PUT: the body is the ruleset exactly as the remote just returned
    # it, so the probe cannot weaken the protection it is measuring. It answers
    # whether the app's own credential holds ruleset write, which is the
    # residual risk to record.
    status, body = call("GET", f"/repos/{repo}/rulesets/{args.ruleset}", token)
    if status != 200:
        print("could not read the ruleset back, so the no-op PUT was not attempted")
        return 1

    current = json.loads(body)
    no_op = {
        "name": current["name"],
        "target": current["target"],
        "enforcement": current["enforcement"],
        "conditions": current["conditions"],
        "rules": current["rules"],
        "bypass_actors": current["bypass_actors"],
    }
    show(f"PUT /repos/{repo}/rulesets/{args.ruleset} (no-op: the same definition)",
         *call("PUT", f"/repos/{repo}/rulesets/{args.ruleset}", token, no_op), limit=600)

    show(f"GET /repos/{repo}/rulesets/{args.ruleset} (unchanged after the no-op PUT)",
         *call("GET", f"/repos/{repo}/rulesets/{args.ruleset}", token))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
