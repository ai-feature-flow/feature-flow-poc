"""Probe a GitHub App: mint a JWT from the private key, list installations.

Used to discover the installation id without needing a user credential.
Reads the key from a path outside the repository (floor F2).
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

import jwt


def mint_jwt(app_id: str, key_path: str) -> str:
    with open(key_path, "r", encoding="utf-8") as handle:
        private_key = handle.read()
    now = int(time.time())
    return jwt.encode(
        {"iat": now - 60, "exp": now + 540, "iss": app_id},
        private_key,
        algorithm="RS256",
    )


def get(url: str, token: str) -> object:
    request = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "feature-flow-spike2",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", "replace")
        print(f"HTTP {error.code} for {url}\n{body}", file=sys.stderr)
        raise SystemExit(1) from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--key", required=True)
    args = parser.parse_args()

    token = mint_jwt(args.app_id, args.key)

    app = get("https://api.github.com/app", token)
    print(f"app:      {app.get('name')} (id {app.get('id')})")
    print(f"owner:    {(app.get('owner') or {}).get('login')}")
    print(f"html_url: {app.get('html_url')}")

    installations = get("https://api.github.com/app/installations", token)
    if not installations:
        print("\nno installations yet - install the app on the org first:")
        print(f"  {app.get('html_url')}/installations/new")
        return 1

    print("\ninstallations:")
    for installation in installations:
        account = (installation.get("account") or {}).get("login")
        print(
            f"  id={installation['id']} "
            f"account={account} "
            f"type={installation.get('target_type')} "
            f"repos={installation.get('repository_selection')}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
