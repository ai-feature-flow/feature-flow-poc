# SP5: an agent step with no model credential in its environment

Status: plan and checks pre-registered 2026-11-01 (commit `21cd16f`), before
any run; the fresh `HOME` added by amendment (commit `5624b2e`) after trial 1.
Run the same day. **The claim holds for Claude Code**: with only a per-step
token and a proxy URL, it completed the step in five of five trials, and it
could not work without the proxy's credential. Trial 1 found that building the
runtime's environment from scratch is not enough: its shell read the user's
startup files (section 5.2).

## 1. Claim under test

Floor F6 (`docs/05` section 1): model credentials stay out of the sandbox. In
`proxy` mode (`docs/07` section 2, `model_access`), the agent runtime talks to a
model proxy, and the proxy adds the credential to each model request. The agent
holds only a per-step token that is worthless outside the proxy.

The claim is that **Claude Code completes an agent step this way**, with no
model credential anywhere in its environment, and that it cannot fall back to
another credential. Risk R6 (credential exposure) in `docs/10` section 2.

**Scope decided by the product owner, 2026-11-01:** Claude Code only, with the
proxy holding the existing Claude Code subscription token
(`CLAUDE_CODE_OAUTH_TOKEN`). No Anthropic API key is available, so Copilot CLI
is not tested here and stays unevidenced for F6.

## 2. What is built

`spikes/08-model-proxy`, in Python.

| Part | Behaviour |
|---|---|
| Model proxy | Listens on `127.0.0.1:7801`. Accepts a request only if it carries the step's token. Replaces that token with the subscription token, adds the OAuth beta header, forwards to `api.anthropic.com`, and streams the response back. Logs each request's path, model, status and token usage, never a credential |
| Agent environment | Built from scratch: `PATH`, `HOME`, a fresh `CLAUDE_CONFIG_DIR`, `ANTHROPIC_BASE_URL` pointing at the proxy, and `ANTHROPIC_AUTH_TOKEN` set to a random per-step token. `CLAUDE_CODE_OAUTH_TOKEN` and `ANTHROPIC_API_KEY` are absent |
| Agent | `claude -p`, model `haiku`, user settings excluded. Tools: `Write`, and `Bash` limited to `env`. Task: write `notes.md` with three bullet points on what `add(a, b)` should do, and save the output of `env` to `env.txt` |
| Harness | Starts the proxy, runs the agent, then checks the evidence. It compares secrets by substring and never prints them |

## 3. Pre-registered checks

Five trials.

**Exercised:** the proxy forwarded at least one model request in every trial,
and every forwarded request carried the step's token.

**Claim checks** (every trial):

| # | Check | Pass |
|---|---|---|
| C1 | The step completed | `notes.md` exists with content, `env.txt` exists, and the agent exited 0 |
| C2 | No credential in the agent's environment | The environment the harness passed has neither `CLAUDE_CODE_OAUTH_TOKEN` nor `ANTHROPIC_API_KEY`. `env.txt`, the agent's own view, contains neither name nor the subscription token's value |
| C3 | All model traffic went through the proxy | The proxy forwarded at least as many requests as there are distinct assistant messages in the transcript |
| C4 | The credential never surfaced | The subscription token's value appears nowhere in the transcript, the sandbox or the proxy log |

**Negative controls.** Each must make C1 fail, in 2 of 2 trials:

| # | Change | Shows |
|---|---|---|
| N1 | `ANTHROPIC_BASE_URL` points at a closed port | Claude Code does not fall back to the subscription login in the macOS keychain or anywhere else |
| N2 | The proxy forwards the step's token instead of adding the real one | The credential that works is the proxy's, not one the agent found |

**Amendment, made after trial 1 and before the remaining trials.** Trial 1
failed C2. The harness built the agent's environment from scratch, but kept
`HOME` as the user's home. Claude Code's Bash tool runs commands in a shell that
reads `~/.zshenv`, which exports 15 credential variables, including
`CLAUDE_CODE_OAUTH_TOKEN`, loading their values from the keychain. In that
shell every value came out empty, so nothing leaked, but the names were there,
and nothing stopped the values from loading. A sandbox gives the agent its own
home, so trials from 2 onwards (T-home) set `HOME` to a fresh, empty directory.
C2 is unchanged. Trial 1 is kept as run; trial 1 is re-run as trial 6 so there
are five T-home trials.

## 4. Out of scope

- Copilot CLI (section 1).
- Sandbox isolation. The agent runs as the same macOS user, so a tool that can
  read the keychain could reach the stored login. Keeping the keychain out of
  reach is the sandbox provider's job (`docs/02` section 4); here the agent is
  given no tool that can read it.
- The `per_run_key` mode, spend limits, and recording cost from the proxy
  beyond counting tokens.

## 5. Results

Raw evidence per trial in `spikes/08-model-proxy/runs/<variant>-<trial>/`:
`summary.json` (checks), `proxy.jsonl` (each forwarded request, status and
model; no credentials), `transcript.jsonl` and the sandbox. Token values in the
saved `env.txt` files and transcripts (the worthless step token and Claude
Code's local messaging token) were redacted before commit; the subscription
token appears nowhere. Run with `python3 run.py <trial> [--variant n1|n2]
--work <dir>` in `spikes/08-model-proxy`.

### 5.1 Main trials (fresh `HOME`, trials 2 to 6)

| Check | Result |
|---|---|
| Exercised: the proxy forwarded model requests, all with the step token | 5 of 5 (3 requests each, all status 200, model `claude-haiku-5-5`) |
| C1 step completed | 5 of 5, about 12 s each |
| C2 no credential in the agent's environment | 5 of 5. The agent's `env` showed only Claude Code's own variables, the proxy URL, the step token, `HOME`, `PATH` and shell basics |
| C3 all model traffic through the proxy | 5 of 5: 3 forwarded requests for 3 assistant messages |
| C4 the credential never surfaced | 5 of 5 |

### 5.2 Trial 1: the user's home

With `HOME` left as the user's home, C2 failed. The environment the harness
passed was clean, but Claude Code's Bash tool runs commands in a shell that
reads `~/.zshenv`. That file exports 15 credential variables, among them
`CLAUDE_CODE_OAUTH_TOKEN`, a GitHub personal access token, Sumo Logic and New
Relic keys, and a Portkey (LLM gateway) key, loading each value from the
keychain. In the agent's shell every value came out empty, so nothing leaked in
this trial. Nothing in the setup would have stopped the values from loading.

The design consequence: keeping credentials out of the runtime's process
environment does not keep them out of the agent's tools. The sandbox must give
the runtime a home and shell startup files that the engine controls (DR19).

### 5.3 Negative controls

| Control | Target | Result |
|---|---|---|
| N1 proxy unreachable | C1 | Fail, 2 of 2: the agent exited 1 after about 180 s of retries, without falling back to the keychain login |
| N2 proxy forwards the step token, not the real one | C1 | Fail, 2 of 2: the API answered 401 and the agent exited 1 |

### 5.4 What changes in the design

| Finding | Change | Where |
|---|---|---|
| Claude Code completes a step in `proxy` mode with no credential in its environment, and cannot fall back to another | R6 (credential exposure): floor F6 evidenced for Claude Code in `proxy` mode | `docs/10` section 2 |
| The runtime's shell reads the home directory's startup files | The sandbox gives the runtime a home and shell startup files controlled by the engine; scrubbing the process environment is not enough | `docs/10` section 1.2, DR19 |
| Python's TLS stack rejected the network's TLS inspection root until given the system roots, and then only with strict X.509 mode off | Engine components that call model or code-host APIs use the platform's trust store | `docs/10` section 1.2, DR20 |

### 5.5 Limits of the evidence

- Claude Code only; Copilot CLI is unevidenced for F6 (section 1).
- The agent ran as the same macOS user, with no tool that reads the keychain;
  sandbox isolation was not tested (section 4).
- The proxy held a subscription token, not an API key; `per_run_key` mode was
  not tested.
- Harness fixes during the runs: the proxy needed the system root certificates
  and strict X.509 mode off, which took two attempts at trial 1 before it
  reached the API.

## 6. Open questions

None.
