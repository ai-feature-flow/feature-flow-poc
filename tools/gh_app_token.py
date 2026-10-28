"""Mint a GitHub App installation token and report its metadata.

Verifies the R6 credential chain end to end:
  private key (outside the repo) -> JWT -> installation token -> scoped, expiring

Never prints the token itself. Exits non-zero if any step fails.
"""

import argparse
import calendar
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


def request(method: str, url: str, token: str, payload: object = None) -> tuple:
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(
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
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", "replace")
        print(f"HTTP {error.code} {method} {url}\n{body}", file=sys.stderr)
        raise SystemExit(1) from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--installation", required=True)
    parser.add_argument(
        "--emit",
        action="store_true",
        help="print only the raw token to stdout, for piping into another tool",
    )
    args = parser.parse_args()

    jwt_token = mint_jwt(args.app_id, args.key)
    # Diagnostics go to stderr so --emit can be piped cleanly.
    print(f"jwt:          minted for app {args.app_id} (valid 10 min)", file=sys.stderr)

    status, token_data = request(
        "POST", f"{API}/app/installations/{args.installation}/access_tokens", jwt_token
    )
    token = token_data["token"]
    expires_at = token_data["expires_at"]

    if args.emit:
        print(token)
        return 0

    print(f"token:        minted (length {len(token)}, value withheld)")
    print(f"expires_at:   {expires_at}")

    expires_epoch = calendar.timegm(time.strptime(expires_at, "%Y-%m-%dT%H:%M:%SZ"))
    remaining = expires_epoch - time.time()
    print(f"ttl:          {remaining / 60:.0f} min")

    print("\npermissions:")
    for name, access in sorted(token_data.get("permissions", {}).items()):
        print(f"  {name}: {access}")

    print("\nrepositories:")
    for repo in token_data.get("repositories", []):
        print(f"  {repo.get('full_name')}")
    if not token_data.get("repositories"):
        print("  (all repositories on the installation)")

    print(f"\nrepository_selection: {token_data.get('repository_selection')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
