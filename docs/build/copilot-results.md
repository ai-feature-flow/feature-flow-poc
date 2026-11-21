# Copilot results: Copilot CLI on Copilot quota

Built 2026-11-22 on `stuart-parkinson_xero/feature-flow-copilot` from a spike, three parallel builders, an end-to-end
test, an independent review, a fix pass, a fix for the classification of a refused key, and a second review's
allow-list test. Feature Flow's Copilot CLI runtime now spends the product owner's GitHub Copilot quota (his Xero
Enterprise Managed User account) through the engine's model proxy, and the agent still holds no credential (F6). One
real Copilot step ran and committed on the fixture repository with the token's value nowhere in its sandbox. The
console run of `std:templates/ticket_delivery` is the product owner's; its steps are in `a4-results.md`, "Live run
with Copilot CLI". No replay history changed.

## What was built

| Item | Change |
|---|---|
| Proxy | `model_access.auth: github`: the proxy sends the key as `Authorization: Bearer`, with no Anthropic header; the allow-list is by method and path, by auth (`github`: `GET meta`, `GET models`, `POST v1/messages`, `POST v1/messages/count_tokens`, `POST chat/completions`), pinned exactly by a test; OpenAI-shaped usage is read; the model route drops one leading `ghs_` from the step token. Composition refuses an unknown `auth`, `auth: github` without an explicit https `upstream`, and `github` on `claude_code` (LOG 496) |
| Runtime | `CopilotCliRuntime` in native mode for `github`: `COPILOT_GITHUB_TOKEN=ghs_<step token>`, `COPILOT_API_URL=<model base url>`, no offline mode or custom provider; `api_key` and `oauth` unchanged. `COPILOT_HOME` is the step's own (`/ff/copilot/<hash of the step key>`), emptied at a new session, and at a continuation cleared of everything but what `--resume` reads, so nothing the agent adds outlives its session (M8 follow-up 15). The image pins Copilot CLI 1.0.107 (LOG 497) |
| Refused key | A failed session whose model requests were all refused 401 or 403 by the upstream, by the engine's own records, ends the step as the model's refusal, a configuration fault, whatever the CLI printed (LOG 499) |
| Tests | Unit and contract tests for both runtimes; Integration: the real CLI 1.0.107 in Docker through the real proxy to an https stand-in for the Copilot API (key only at the stand-in; the key nowhere in the sandbox, homes, transcript or commits), a refused key, and a continuation that drops what the agent planted in its home; Live: C1 on `FF_COPILOT_GITHUB_TOKEN` and `FF_COPILOT_API_URL` (LOG 498) |
| Documents | `docs/04`, `docs/06` s3.2 and s3.13, `docs/07` (`auth: github`; s6.1, a local-profile installation for Copilot), `a4-results.md` "Live run with Copilot CLI", `CONTRIBUTING.md`, LOG 496 to 499 (unreviewed) |

## Live check

Run once, 2026-11-22, on a local Temporal dev server, and passed:
`LiveCopilotTests.A_copilot_implementer_step_on_the_fixture_repository_commits_and_its_sandbox_holds_no_credential` (59 s).

| Evidence | Value |
|---|---|
| Account's Copilot API (`endpoints.api` of `copilot_internal/user`, no quota) | `https://api.enterprise.githubcopilot.com`, plan `enterprise` |
| Model | `claude-haiku-4.5` (offered with `/v1/messages`) |
| Run | `run-8d997a4bede1`; the work branch reached `1a99c6d125`; the report was accepted; one session, ended completed |
| Transcript | session 1: `sha256:38635e3225028f822ef73ae9992e5dc74547ae2e02f1c0b7b854b5cf8c54d6ec` |
| Model requests through the proxy | 9: `GET /meta` 200, `GET /models` 200, `POST /v1/messages` 200 ×7 |
| Usage recorded | 42 input tokens, 1,309 output tokens (Copilot's premium requests are GitHub's to count, not the engine's) |
| Hook calls | 16 |
| Credential | the token's value in none of the sandbox's environment, any process's environment (the CLI's own was readable), the homes, the transcript or the commits; nor in the test's output or log |
| Clean-up | the test removed its branch and hidden reference from `ai-feature-flow/feature-flow-example` (none left) |

After the merge the three hosts also started with `auth: github` and the real upstream, the console answered and
listed `std:templates/ticket_delivery`, and the model route refused a request without a step token (401).

## Checks

Build 0 warnings. Default suite 26 projects passed (Replay 44); Integration category on a local Temporal dev server
passed (Integration.Tests 131, Postgres 30, S3 12, Docker 17, API 2). Each new guard was seen to fail with the code it
guards broken: about 45 breaks by the builders, the fix passes and the reviewers, and five on the merged branch by the
sub-orchestrator (no Bearer key for `github`; no `COPILOT_API_URL`; 429 counted as a refusal; an added entry in each
allow-list).

## Decisions accepted by the product owner

- The proxy relays Copilot CLI's own traffic and holds the token (no token in the sandbox, which reaches the internet).
- The enterprise's Copilot CLI policies, which the CLI cannot fetch in the sandbox, are not applied there; the engine
  is the policy boundary.

## Follow-ups

1. `COPILOT_API_URL` is undocumented; the version is pinned and C1 catches its removal. Re-run C1 after any bump.
2. The step token also goes to `api.github.com` and, as `GET /agents/tasks/<id>`, to the agent endpoint's origin (404);
   accepted in LOG 496.
3. Tighten C1's model check from "at least one `POST` 200" to "every request 200" now that a real log exists; run N1
   and N2 once when quota allows.
4. Compare the run's premium requests with a normal Copilot session (spike 0, open question 5).
