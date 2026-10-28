"""Minimal GitHub REST helper authenticated as a GitHub App installation.

Mints a JWT from the App private key (stored outside the repository, floor F2),
exchanges it for a short-lived installation token, and issues REST calls.

Usage:
    python tools/gh_app_api.py --app-id ID --key PATH --installation ID \
        --method POST --path /repos/OWNER/REPO/issues --body '{"title":"x"}'
"""

import argparse
import calendar
import json
import sys
import time
import urllib.error
import urllib.parse
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


def installation_token(app_id: str, key_path: str, installation: str) -> str:
    request = urllib.request.Request(
        f"{API}/app/installations/{installation}/access_tokens",
        method="POST",
        headers={
            "Authorization": f"Bearer {mint_jwt(app_id, key_path)}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "feature-flow-spike2",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))["token"]


def call(method: str, path: str, token: str, body: str | None) -> int:
    url = path if path.startswith("http") else f"{API}{path}"
    data = body.encode("utf-8") if body else None
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
            payload = response.read().decode("utf-8")
            print(f"HTTP {response.status}")
            if payload.strip():
                print(payload)
            return 0
    except urllib.error.HTTPError as error:
        print(f"HTTP {error.code}", file=sys.stderr)
        print(error.read().decode("utf-8", "replace"), file=sys.stderr)
        return 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--installation", required=True)
    parser.add_argument("--method", default="GET")
    parser.add_argument("--path", required=True)
    parser.add_argument("--body", default=None)
    args = parser.parse_args()

    token = installation_token(args.app_id, args.key, args.installation)
    return call(args.method, args.path, token, args.body)


if __name__ == "__main__":
    raise SystemExit(main())
