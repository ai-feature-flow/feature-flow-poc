# Spike 2 results: one real ticket run

Written 2026-10-28.

**Question** (`docs/08` 2.2): does one ticket run end to end against real
providers, on a real repository?

**Answer: yes.** One ticket ran the real 17-step `ticket-delivery` plan against
real GitHub, real GitHub Actions, and two real agent runtimes, and merged
**PR #3** into `main` as squash commit `5dd0108`. The cost of the run is known
from the record store: **6,150 input tokens, 111 output tokens, 103.3 s of
compute across 10 distinct steps**.

| Deliverable | Where |
|---|---|
| This document | `docs/16-spike-2-results.md` |
| Spike code | `spikes/02-real-run/` |
| Fixture repository | `ai-feature-flow/feature-flow-example` |
| The plan | `spikes/02-real-run/plans/ticket-delivery.yaml` |
| Configuration | `spikes/02-real-run/plans/organisation.yaml` |
| Check output | `spikes/02-real-run/results/spike2-checks.md` |
| Run narrative | `spikes/02-real-run/results/spike2-real-run.md` |
| Output sizes | `spikes/02-real-run/results/outputs-sizes.json` |

---

## 1. What was built

Spike 1 validated the interpreter against a **fake** sandbox. Spike 2 replaces
it with real providers, behind the same `ISpikeActivities` seam. **The workflow
itself is unchanged** — that is the point of the seam, and it is the strongest
evidence that the abstraction is right.

| Project | What it is |
|---|---|
| `FeatureFlow.Spike.Providers` | Secrets by reference (F2) and the credential broker (R6) |
| `FeatureFlow.Spike.CodeHost` | `ICodeHostProvider` against the real GitHub REST API |
| `FeatureFlow.Spike.Ci` | `ICiProvider` against real GitHub Actions |
| `FeatureFlow.Spike.AgentRuntime` | Two named instances: `copilot-cli` and `claude-code` |
| `FeatureFlow.Spike.RealWorker` | `ISpikeActivities` over the real providers |
| `FeatureFlow.Spike.RealHarness` | Drives the run and the ten checks |

The fixture repository is a small .NET 9 API with two test tiers that have
**deliberately different execution targets**: `unit` runs in the sandbox,
`integration` is Docker-dependent and is delegated to CI. That difference is
the property R5 exists to test.

---

## 2. The ten checks

All ten pass. Full output is in `results/spike2-checks.md`; the substance is
below.

### R5 — a Docker-dependent tier runs through CI delegation

The manifest declares `integration` as `target: ci` with
`ci: { instance: github-actions, workflow: integration.yml }`. The step
dispatched that workflow at the branch head and returned in **22.7 s without
waiting for the run**. Polling then observed `Completed / Success` with 1/1
checks registered. The workflow the engine dispatched is the one the manifest
names, and it runs the tier's own command:

```
docker compose -f compose.integration.yaml up --abort-on-container-exit --exit-code-from integration-tests
```

That is the R5 property: the Docker-dependent tier ran in the environment CI
would use, not in a sandbox that resembles it.

### R6 — credentials are short-lived, scoped to one repository, and expire

A credential minted for the `implement` step:

```
scope:      repo:ai-feature-flow/feature-flow-example contents:write metadata:read pull_requests:write
issued at:  2026-10-28T16:25:37Z
expires at: 2026-10-28T17:25:37Z   (from the remote, not computed)
ttl:        60.0 min
actor:      agent:claude-code (run final-run, step implement)
```

Expiry is assertable: not expired one minute after issue, expired one minute
past expiry. Three wider requests were **refused**, not granted — a wider
access level, another repository, and a TTL over the maximum. The broker
refuses rather than silently narrowing, because a truncated grant is
indistinguishable from a correct one at the call site.

### R13 — tokens, model and compute time recorded per step

14 cost records across 10 distinct steps. The three `agent.run` steps report
model and tokens from the runtime's own usage output; the orchestration steps
report compute time.

| step | kind | model | in | out | compute |
|---|---|---|---|---|---|
| plan_spec | agent.run | qwen2.5-coder:7b | 2050 | 57 | 6.9 s |
| design | agent.run | qwen2.5-coder:7b | 2050 | 28 | 6.7 s |
| implement | agent.run | qwen2.5-coder:7b | 2050 | 26 | 17.8 s |
| run_tests | test.run_tiers | – | – | – | 45.1 s |
| review | review.run | – | – | – | 2.3 s |
| evaluate_readiness | findings.evaluate_readiness | – | – | – | 0.9 s |
| open_pr | scm.open_pr | – | – | – | 8.1 s |
| wait_checks | ci.wait_checks | – | – | – | 2.4 s |
| merge_pr | scm.merge | – | – | – | 5.8 s |
| verify_fix | fix.verify | – | – | – | 7.5 s |

`verify_fix` ran four times and `open_pr` twice — the fix loop and a retry,
both visible in the record rather than hidden.

### R4 — a review run twice on unchanged input gives the same verdict

Same diff hash, same prior findings, two passes: both `findings_open` with the
same single finding. The control is the interesting part — decline the finding
and the verdict changes to `completed`, and the declined finding is **not
re-raised** on the next review. Determinism holds because `prior_findings` is
passed; without it the verdict would drift.

### R16 — a mid-run edit to `feature-flow.yaml` is picked up by the next step

Step N read the manifest at `2f507523…` (985 bytes). A mid-run edit committed
`1bfe649a…`. Step N+1 read `d4127d26…` (1015 bytes) — a different hash, with
the edit visible in the content it read. 12 of 13 cost records carry the manifest
hash they used.

### R8 — a gate left open does not time out, and a reminder fires

A gate was left open and polled: still waiting at t+120 s with no terminal state,
and six reminders fired while it waited. Approving it completed the run.

**Honest scope note:** this waited **2 minutes**, not hours. The mechanism that
survives hours is the durable wait itself — the workflow holds no timer and no
activity while a gate is open, so there is nothing to time out. The check evidences
the mechanism, not the duration.

### K4 — seed a finding, fix it, run `fix.verify`

A finding was seeded at `WidgetStore.cs`. Three cases:

| case | regression test | `fix.verify` |
|---|---|---|
| fix with no regression test | absent | **fails** |
| same fix with a regression test | present | passes |
| "not applicable" with no reason | – | **fails** |

The regression-test requirement is a **step invariant**, not a template switch
(review 09 C4). The escape hatch is carried in the finding's triage record, so
a template cannot relax the verification floor. The fix is checked against the
original finding on the **full diff**, not only the changed lines.

### review 11 C1 — actual `outputs` sizes per step kind

Measured from the real run's carried state:

| kind | steps | entries | max value bytes | total bytes | vs cap |
|---|---|---|---|---|---|
| agent.run | 4 | 5 | 40 | 104 | within |
| ci.wait_checks | 1 | 2 | 7 | 8 | within |
| findings.evaluate_readiness | 1 | 2 | 4 | 5 | within |
| findings.triage | 1 | 2 | 6 | 7 | within |
| fix.verify | 1 | 2 | 59 | 65 | within |
| human.gate | 4 | 1 | 8 | 8 | within |
| plan.replan | 1 | 1 | 26 | 26 | within |
| review.run | 1 | 3 | 71 | 81 | within |
| scm.merge | 1 | 3 | 40 | 49 | within |
| scm.open_pr | 1 | 2 | 40 | 41 | within |
| test.run_tiers | 1 | 3 | 16 | 34 | within |

**The cap is not binding.** The widest single value was 71 bytes against a
256-byte cap; the most entries was 5 against 16. The measurement therefore
does not justify *changing* the cap — it justifies **keeping** it. A cap that
never binds is a cap with room to grow, which is what a starting value should be.

### K4 — the decline path

A finding was declined with a recorded reason, posted to PR #4:

```
reason: accepted: the id sequence is internal to the store and is not part of the API contract
```

The finding is recorded as declined, the next review's prior findings exclude
it, and it is **not re-raised**. The verdict with it declined is `completed`.

### K5 — approve a gate from chat, then drop one webhook and poll instead

Part 1: a gate was approved through the inbound event path, delivered as a
Temporal update. Result `completed`.

Part 2: the same gate, with the inbound event **deliberately not delivered**.
The gate stayed open, and the polling fallback read the remote state and
resolved it to `approved` in 2 polls. Result `completed`.

**Honest scope note:** the drop is simulated by not delivering the update. A
real drop would be a lost inbound HTTP delivery. The fallback is the same
either way, because the step's source of truth is the remote state, not the
event — which is exactly what "events wake, reads confirm" (`docs/04` 1.3.1)
requires.

---

## 3. The `insert_after` measurement (review 11 C2)

`docs/09` 15.3.1 argues for `insert_after` with rewiring over `add_step` plus
`change_route`, on the grounds that it gives an LLM fewer things to get right.
This is the measurement that settles it.

| form | validation failure rate |
|---|---|
| `insert_after` | 25% |
| `add_step` + `change_route` | 50% |
| both, with `protected` cleared | 0% |

**Read this carefully, because the headline number is misleading.** The
two-phase result separates two different things:

- **Phase 2 — rewiring correctness: both forms are 100% reliable.** Neither
  form ever produced a wrong graph. The argument in `docs/09` 15.3.1 was about
  *authoring difficulty*, not engine correctness, and the engine gets both
  right.
- **Phase 1 — the entire gap is floor F3.** `change_route` on a protected
  step is refused. The two-op form therefore gets refused **twice** where
  `insert_after` is refused **once** — it is the same single refusal, counted
  once per operation.

So the measurement supports the `docs/09` 15.3.1 decision, but for a narrower
reason than the document gives: not that `add_step` + `change_route` rewires
incorrectly, but that it **doubles the author's exposure to a protected-step
refusal** and requires knowing every inbound route. The document's reasoning
survives; the evidence for it is different in kind than the document implies.

---

## 4. Runtime capabilities: the comparison `docs/08` 2.2 asked for

Both runtimes implement the same `IAgentRuntimeProvider`. What they actually
provided differs — this is the table the spike exists to produce.

| Capability | `copilot-cli` | `claude-code` |
|---|---|---|
| `structured_output` | yes | yes |
| `streaming` | yes | yes |
| `tool_allowlist` | yes | yes |
| `hooks` | **partial** | **yes** |
| `session_resume` | yes | yes |
| `usage_reporting` | yes | yes |
| `model_configuration` | yes | yes |
| `custom_endpoint` | yes (BYOK router) | yes (`ANTHROPIC_BASE_URL`) |
| `max_context_tokens` | unknown (not exposed) | 200000 |

**The one real difference is hooks.** Claude Code fired all four events
(`SessionStart`, `PreToolUse`, `PostToolUse`, `Stop`) on Windows. Copilot CLI
fired `SessionStart` and `Stop`; `PreToolUse` and `PostToolUse` registered but
never fired in these runs.

That matters because **per-tool-call policy (deny before execution) depends on
`PreToolUse`.** On Copilot CLI it is therefore unavailable, and the
`docs/08` 2.5 fallback applies: sandbox-enforced write access, broker-scoped
push, post-step diff check, and cost from the runtime's own usage output. The
run records the coarser control rather than assuming the finer one.

**Caveat, stated rather than smoothed over:** the Copilot CLI runs here used a
local Ollama model through a BYOK router, because no Copilot subscription token
is available in this environment. The local models emit tool calls as *prose
text* rather than structured `tool_use` blocks, so the CLI may never have
dispatched a real tool — which would explain the missing `PreToolUse` events
without implying anything about Copilot CLI's behaviour with a first-party
model. **This is unproven either way and should be re-tested with a real Copilot
credential before it is treated as a finding about the runtime.**

---

## 5. Bugs found

Six, all found by running rather than reading. Two are worth calling out.

1. **`CopilotCliAgentRuntime` failed on multi-line prompts.** A 29-character
   two-line prompt made the CLI exit 1 with no message, while a 610-character
   single-line prompt succeeded. This silently broke `plan_spec` in every early
   run — the failure looked like a model problem, not an argument-quoting bug.

2. **A work failure on `scm.merge` ended the run silently.** `scm.merge`
   declares `blocked`, not `failed`, so a `failed` result had no route and the
   workflow completed **looking like a success**. This is the dangerous one: it
   hid a non-merge behind a "completed" result. It is the same class as the
   prototype's defining failure — an interruption becoming a wrong outcome
   rather than a continuation.

The other four: `Route` ignored `{ resume: true }` (so `plan.replan` re-ran
forever); `AddReviewCommentAsync` could not post PR-level comments (GitHub
requires positioning); manifest parsers sliced from a fixed offset; and the
code host was given a bare repo name as its scope.

---

## 6. What could not be evidenced

**The model behind the agent runtime cannot author code.** Ollama returns tool
calls as prose text rather than structured `tool_use` blocks, so the runtime
never acts on them. Verified directly: `qwen2.5-coder:32b` returns
`stop_reason=end_turn` with the call sitting in `content[0].text`.

No Copilot or Anthropic credential exists in this environment, and the GitHub
App token cannot authenticate to Copilot's models. So in the merged run, each
agent step's **content** was applied by `StepWork` rather than produced by the
model.

Everything *around* the model is real: the runtime process was started and
polled, the commit was pushed, the diff was read back from the code host, CI
was dispatched and its result read, and the PR was opened and merged. What is
not evidenced is **model-authored code**.

This is the single largest gap in the spike, and it is a credential gap, not a
design gap. It should be closed before Spike 3.

---

## 7. Numbers this spike replaces

| Was | Now | Source |
|---|---|---|
| `outputs` cap of 16 entries / 256 bytes, a starting value | Measured: widest value 71 B, most entries 5. Cap not binding; keep it | review 11 C1 |
| `insert_after` rewiring argued, not measured | 25% vs 50% validation failure rate; both 100% correct at rewiring | review 11 C2 |
| Runtime capabilities assumed | Measured per runtime; hooks differ, `PreToolUse` unproven on Copilot CLI | `docs/08` 2.2 |
| Sandbox/CI divergence (R5) | Docker-dependent tier delegated to CI, verified green | R5 |
| Credential scoping asserted | 60 min, one repository, expiry assertable, wider requests refused | R6 |

---

## 8. Open questions

| # | Question |
|---|---|
| 1 | Should the App installation be tightened from org-wide to `feature-flow-example` only? It is currently org-wide, which is wider than the spike needs |
| 2 | Can we get a Copilot subscription token or an Anthropic key, so Spike 3 exercises model-authored code rather than `StepWork`? |
| 3 | Is `PreToolUse` genuinely absent on Copilot CLI, or an artefact of the local model? Needs a first-party credential to settle |
| 4 | Four closed PRs (#1–#4) remain on the fixture repo; GitHub has no API to delete a PR. Delete them in the web UI, or leave them as the example's history? |
| 5 | `max_context_tokens` is unknown for Copilot CLI. Do we need it, or is the runtime's own usage output enough? |
