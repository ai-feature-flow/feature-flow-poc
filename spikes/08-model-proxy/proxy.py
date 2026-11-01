"""Model proxy for SP5: the agent sends the step's token; the proxy swaps in the
real credential and forwards to the Anthropic API, streaming the reply back.
It never logs a credential.

Environment:
  PORT            listen port on 127.0.0.1
  STEP_TOKEN      the only token the proxy accepts from the agent
  UPSTREAM_TOKEN  the real credential (the Claude Code subscription token)
  PROXY_LOG       JSON-lines log path
  PROXY_MODE      "normal", or "passthrough" for control N2 (forward the step token)
"""
import http.client
import json
import os
import re
import ssl
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UPSTREAM = "api.anthropic.com"
STEP_TOKEN = os.environ["STEP_TOKEN"]
UPSTREAM_TOKEN = os.environ["UPSTREAM_TOKEN"]
LOG = os.environ["PROXY_LOG"]
MODE = os.environ.get("PROXY_MODE", "normal")
# The trusted roots exported from the macOS system keychains: a corporate TLS
# inspection root is not in Python's own bundle.
TLS = ssl.create_default_context(cafile=os.environ.get("CA_BUNDLE") or None)
# That root lacks an Authority Key Identifier, which Python's strict X.509 mode
# rejects. Chain and hostname verification stay on.
TLS.verify_flags &= ~ssl.VERIFY_X509_STRICT
OAUTH_BETA = "oauth-2025-04-20"
HOP_BY_HOP = {"host", "authorization", "x-api-key", "content-length", "connection", "accept-encoding",
              "transfer-encoding", "keep-alive", "proxy-connection"}


def log(**record):
    with open(LOG, "a") as f:
        f.write(json.dumps({"t": round(time.time(), 3), **record}) + "\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        self.forward()

    def do_POST(self):
        self.forward()

    def forward(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        presented = (self.headers.get("Authorization") or "").removeprefix("Bearer ").strip() or \
            (self.headers.get("x-api-key") or "").strip()
        model = None
        try:
            model = json.loads(body).get("model") if body else None
        except (json.JSONDecodeError, AttributeError):
            pass
        if presented != STEP_TOKEN:
            log(method=self.command, path=self.path, model=model, status=401, step_token_ok=False)
            self.reply(401, b'{"type":"error","error":{"type":"authentication_error","message":"unknown step token"}}')
            return

        headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP_BY_HOP}
        credential = STEP_TOKEN if MODE == "passthrough" else UPSTREAM_TOKEN
        headers["Authorization"] = f"Bearer {credential}"
        beta = [b.strip() for b in (headers.pop("anthropic-beta", None) or headers.pop("Anthropic-Beta", None) or "").split(",") if b.strip()]
        if OAUTH_BETA not in beta:
            beta.append(OAUTH_BETA)
        headers["anthropic-beta"] = ",".join(beta)
        headers["Accept-Encoding"] = "identity"
        headers["Content-Length"] = str(len(body))

        connection = http.client.HTTPSConnection(UPSTREAM, timeout=600, context=TLS)
        connection.request(self.command, self.path, body=body, headers=headers)
        upstream = connection.getresponse()

        self.send_response(upstream.status)
        for key, value in upstream.getheaders():
            if key.lower() not in ("transfer-encoding", "connection", "content-length", "content-encoding"):
                self.send_header(key, value)
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()
        seen = b""
        while True:
            chunk = upstream.read1(65536)
            if not chunk:
                break
            if len(seen) < 2_000_000:
                seen += chunk
            self.wfile.write(f"{len(chunk):x}\r\n".encode() + chunk + b"\r\n")
            self.wfile.flush()
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()
        connection.close()

        text = seen.decode("utf-8", "replace")
        inputs = [int(x) for x in re.findall(r'"input_tokens":\s*(\d+)', text)]
        outputs = [int(x) for x in re.findall(r'"output_tokens":\s*(\d+)', text)]
        log(method=self.command, path=self.path, model=model, status=upstream.status, step_token_ok=True,
            input_tokens=max(inputs) if inputs else None, output_tokens=max(outputs) if outputs else None,
            error=text[:200] if upstream.status >= 400 else None)

    def reply(self, status, body):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", int(os.environ["PORT"])), Handler).serve_forever()
