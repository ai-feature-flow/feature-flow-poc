# Spike 2 results: one real ticket run

Written 2026-10-28. Revised 2026-10-29 after review 14, and 2026-10-30 after
reviews 15 and 16.

**Spike 2 is closed.** Review 16, 2026-10-30: *"Spike 2 has answered its
question: yes, the design is feasible against real providers."*

**Question** (`docs/08` 2.2): does one ticket run end to end against real
providers, on a real repository?

**Answer: yes, the design is feasible.** A model authored the change (Copilot
CLI on `z-ai/glm-5.3-flash` through OpenRouter — `WidgetStore.IsEmpty()` and its
regression test), the plumbing around it is real, and a PR was merged through
the engine (PR #3). The two halves were shown in different runs; review 16
accepted that as enough for feasibility.

**How the answer got here.** The first run claimed the exit criterion was met. It
was not: floor F1 was bypassed, and the agent's output was canned content rather
than model-authored work. Review 14 caught both. They are fixed and re-run, and
the corrected state is below. Section 9 records what review 14 changed, and
section 10 carries the design requirements the spike produced.

**What is evidenced.** One ticket ran the real 17-step `ticket-delivery` plan
against real GitHub and real GitHub Actions, with the `copilot-cli` runtime
**authoring the change** (commit `2c04e04`, `WidgetStore.IsEmpty()`) and the
fixer authoring its regression test (commit `1b248db`). The re-run targeted the
integration branch `ff/epic/spike-2`, not `main`. Cost is known from the record
store.

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

**Not met.** The first run's R4 was circular: `review.run` derived its verdict
from the diff hash, so two passes agreed by construction. Review 14 A7 required
re-running with a real reviewer model.

Four real model passes on unchanged input:

| pass | outcome | findings | prior findings passed |
|---|---|---|---|
| 1 | `findings_open` | 1 | 0 |
| 2 | `findings_open` | 1 | 0 |
| 3 | `findings_open` | 1 | 1 |
| 4 | `findings_open` | 2 | 1 |

**The verdicts do not agree, and passing prior findings did not stabilise
them.** Two distinct instabilities:

1. **A fixed finding is re-raised.** `f-review-1` is recorded `fixed=true` after
   the fixer's commit, yet the next pass raises it again — the model is told the
   finding exists and is fixed, and re-raises it anyway.
2. **A new finding appears on unchanged input.** Pass 4 raises
   `f-review-2` (`missing-eof-newline`), which passes 1-3 did not raise.

This is precisely what R4 exists to detect, so it is reported rather than
reconciled. The control still holds: declining a finding changes the verdict,
and a declined finding is not re-raised — enforced by the engine (K4), not left
to the model.

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

The first measurement was **circular** — patches were generated in code by
cycling targets, so the 25%/50% rates measured floor F3 refusals, not planner
reliability. Review 14 A8 required re-running with a planner model authoring
patches from the readable form.

**Re-run: 20 trials per form, planner-authored, against the real plan with
`design` protected.**

| form | authored | planner failures | validation failures | applied |
|---|---|---|---|---|
| `insert_after` | 20/20 | 0 (0%) | 4 (20%) | 16 (80%) |
| `add_step` + `change_route` | 20/20 | 0 (0%) | 5 (25%) | 15 (75%) |

The planner authored **every** patch — 0% planner failure rate on both forms.
That is the number review 11 C2 asked for, and it is a real measurement rather
than a floor-F3 artefact.

The remaining 20%/25% is still floor F3, but now for a more informative
reason: the planner **chose** `plan_spec` as its target, and inserting after
`plan_spec` strands the protected `design` step. The earlier measurement forced
that target by cycling; this one shows the planner picking it on its own.

**What this does not say.** The gap between the forms (20% vs 25%, one trial) is
**not** large enough to conclude that `insert_after` is safer as a mechanism.
With 20 trials, one trial is the smallest difference the measurement can
express. Reporting it as a reliability difference would repeat the mistake A8
is about. The finding is narrower: a planner asked to insert a notification
step will sometimes pick a target whose rewiring strands a protected step, and
the two-op form hits that one extra time because it states the rewiring
explicitly.

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
| `custom_endpoint` | yes (BYOK router) | no (Anthropic API only) |
| `max_context_tokens` | unknown (not exposed) | 200000 |

**Two real differences.**

**Hooks.** Claude Code fired all four events (`SessionStart`, `PreToolUse`,
`PostToolUse`, `Stop`) on Windows. Copilot CLI fired `SessionStart` and `Stop`;
`PreToolUse` and `PostToolUse` registered but never fired in these runs.

This matters because **per-tool-call policy (deny before execution) depends on
`PreToolUse`.** On Copilot CLI it is therefore unavailable, and the `docs/08`
2.5 fallback applies: sandbox-enforced write access, broker-scoped push,
post-step diff check, and cost from the runtime's own usage output. The run
records the coarser control rather than assuming the finer one.

**Custom endpoints.** Copilot CLI honours `COPILOT_PROVIDER_BASE_URL` and will run
against an OpenAI-compatible router. Claude Code validates model names against
Anthropic's own list and speaks the Anthropic Messages API, so it **cannot** be
pointed at an arbitrary router — it rejects the model before any request is made.
This is why A3 is evidenced for `copilot-cli` only.

**Caveat, stated rather than smoothed over:** the Copilot CLI runs here used
`z-ai/glm-5.3-flash` through OpenRouter, because no Copilot subscription
token is available in this environment. Whether `PreToolUse` fires on Copilot CLI
with a first-party model is **unproven either way** and should be re-tested before
it is treated as a finding about the runtime.

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

**Claude Code on A3: not evidenced.** It rejects the router's model with
`[claude-code:unrecognized_model]` (HTTP 404), because it validates model names
against Anthropic's own list and speaks the Anthropic Messages API rather than the
OpenAI-compatible shape this router serves. Per the product owner's decision of
2026-10-29, no proxy was built to work around this. `organisation.yaml`'s
`roleMapping` now maps every role to `copilot-cli`; the `claude-code` instance is
still registered and probed, but maps to no role, because a runtime that cannot
reach the model cannot evidence anything.

**A stable R4 verdict: not evidenced — and that is the finding.** With a real
reviewer model in the loop, verdicts differ across passes on unchanged input
(section 2, R4). The previous pass was an artefact of the verdict being a pure
function of the diff hash. The engine-enforced control still holds: a declined
finding is not re-raised.

**K4-decline: not evidenced.** Opening a pull request returned
`Infrastructure/server_error`, so the decline reason could not be posted on a real
PR. This is pre-existing and unrelated to A3/A7/A8.

**A2 residual risk, recorded not fixed.** The `administration` permission that
lets the app create the `main` ruleset also lets it **edit** that ruleset — a no-op
`PUT` was accepted with HTTP 200. So the code host is a backstop against an engine
that is *wrong*, not one that is *hostile*. Narrowed by: the key lives outside the
repo (floor F2), and the broker's grantable set excludes `administration`, so no
agent can obtain a credential that could edit it. Closing it properly needs an
org-level ruleset or an owner-held credential — a product-owner decision.

---

## 7. Numbers this spike replaces

| Was | Now | Source |
|---|---|---|
| `outputs` cap of 16 entries / 256 bytes, a starting value | Measured: widest value 71 B, most entries 5. Cap not binding; keep it | review 11 C1 |
| Runtime capabilities assumed | Measured per runtime; hooks differ, `PreToolUse` unproven on Copilot CLI | `docs/08` 2.2 |
| Sandbox/CI divergence (R5) | Docker-dependent tier delegated to CI, verified green | R5 |
| Credential scoping asserted | 60 min, one repository, expiry assertable, wider requests refused | R6 |
| F1 held by plan routing | F1 enforced by the engine; a harness approval is refused | review 14 A1 |
| `main` unprotected on the code host | Ruleset `protect-main` active, no app bypass | review 14 A2 |

The `insert_after` row is **deliberately absent**. The first measurement was
circular; the re-run (section 3) reports a 0% planner failure rate on both forms
and a 20%/25% validation failure rate that is still floor F3. Neither number
supports replacing the `docs/09` 15.3.1 estimate with a measured reliability
figure, so no row is claimed.

---

## 8. Open questions

Review 14 answered all five questions from the first draft. The answers are
recorded here, and the new questions they raise are listed below.

### Answers from review 14

| # | Question | Answer |
|---|---|---|
| 1 | Tighten the App installation from org-wide to the fixture repository | **Yes.** Least privilege is the design (R6); install it on `feature-flow-example` only |
| 2 | A Copilot token or Anthropic key | Not for now: use OpenRouter, for both runtimes |
| 3 | Is `PreToolUse` absent on Copilot CLI? | Re-test with a first-party model after A3. Until then, plan for the `docs/08` 2.5 fallback on Copilot |
| 4 | Closed PRs #1–#4 | Leave them as the example's history |
| 5 | `max_context_tokens` for Copilot CLI | Not needed for version 1. The runtime's usage output is enough; record it as unknown |

### Answers from reviews 15 and 16

| # | Question | Answer |
|---|---|---|
| 2 | Claude Code cannot reach OpenRouter | **Deferred by the product owner, 2026-10-29.** No Anthropic credential on the machine, so Copilot CLI is used. The runtime stays pluggable. **Not a spike gap** |
| 3 | The `administration` permission lets the app edit the ruleset | **Parked, to be done shortly.** Recorded as design requirement R3: code host protection is configured by an identity the engine does not hold |
| 4 | Is a larger `insert_after` trial worth running? | **No.** Review 16: spikes prove feasibility; production concerns are recorded as design requirements, not fixed in spike code |
| — | B4 (the `docs/15` 6.3 slot correction) and the K4 decline-path re-run | **Do them only if Spike 3 needs them** |

### Still open

| # | Question |
|---|---|
| 1 | **R4 is genuinely unstable with a real reviewer model** — a fixed finding is re-raised, and a new finding appears on unchanged input. Is this acceptable for version 1, or does the review loop need a stability mechanism (for example, requiring two agreeing passes before a finding is raised)? Review 16 says the findings ledger's handling of re-raised findings is a Spike 3 or design question |

---

## 9. What review 14 changed

The first version of this document claimed the exit criterion was met. It was
not. Review 14 identified four reasons, all of which are now addressed or
recorded.

| # | Review 14 finding | State |
|---|---|---|
| A1 | Floor F1 bypassed: `main` merged by the bot after the harness auto-approved every gate | **Fixed.** The engine enforces F1 itself; a harness approval is refused. Test added. The false comment claiming an integration-branch target is corrected. |
| A2 | No code-host protection for `main` | **Fixed.** Ruleset `protect-main` (id 26099165) is active with no app bypass. Residual risk recorded in section 6. |
| A3 | No real model authored the change | **Partly fixed.** Evidenced for `copilot-cli` on `z-ai/glm-5.3-flash` via OpenRouter; `StepWork` removed from the agent path. **Not evidenced for `claude-code`** — it cannot reach an OpenAI-compatible router. |
| A5 | Plan and organisation config disagree on decision types | **Fixed.** Policy keyed by decision type; run-start checks 20-22 run in the worker. |
| A6 | `policy.replan.allowed_roles` missing | **Fixed.** Added; a disallowed role becomes a pending human decision. |
| A7 | R4 circular | **Fixed and now genuinely failing.** Real reviewer model; verdicts disagree across passes. Reported, not reconciled. |
| A8 | `insert_after` measurement circular | **Fixed.** Planner-authored patches; 0% planner failure rate. The 20%/25% gap is within noise and is not claimed as a reliability difference. |
| B1 | W0 evidence not recorded | **Fixed.** Section 4 carries the per-runtime hook evidence. |
| B2 | `docs/16` overstated the result | **Fixed.** The answer is now "partly". |
| B3 | Closed PRs on the fixture repo | **Noted.** Left as the example's history, per the product owner. |
| B4 | `docs/15` 6.3 slot measurement inconsistent | **Open.** Not started; carried below. |

**Not yet done:** B4, the Spike 1 correction — `docs/15` 6.3 says "roughly 10 activities wait" at 20 against 10, but its table shows 5 and 6; peak never above 9 of 10, and 25 activities for 20 runs are unexplained. Review 16 says do it only if Spike 3 needs it.

---

## 10. Design requirements the spike produced

Review 16 decided that production concerns found during a spike are recorded as
design requirements, not fixed in spike code. These four are now in `docs/08`
section 1.3 and `docs/01` section 4.6.

| # | Requirement | Where recorded |
|---|---|---|
| R1 | **Model credentials never enter the agent's environment.** Spike 2 passed the OpenRouter key as an environment variable with all tools allowed. Version 1 uses a proxy that adds the key, or per-run keys with a limit and expiry | `docs/01` 4.6 |
| R2 | **The F1 floor is enforced by the engine at merge time**, reading the base and default branch from the code host, and backed by code host protection on the default branch | `docs/08` 1.3 |
| R3 | **Code host protection is configured by an identity the engine does not hold.** The engine only checks that the protection exists | `docs/08` 1.3 |
| R4 | **Review verdicts are unstable with a real model.** The findings ledger's handling of re-raised findings is a Spike 3 or design question | `docs/08` 1.2 and 1.3 |

**The one that matters most is R4.** It is the prototype's lesson, now measured
rather than remembered: with a real model in the review loop, the same diff
produces different findings on different passes. Passing prior findings makes
verdicts *more* stable, not stable. Whether the design needs a stability
mechanism is the open question Spike 3 inherits.

---

## 11. What Spike 3 inherits

Review 16: *"Propose the Spike 3 plan (`docs/08` 2.3, concurrent tickets with the
seeded cross-ticket defect) in light of what Spikes 1 and 2 changed, before
building it."*

What changed that Spike 3 should account for:

1. **The provider seam holds.** Spike 2 replaced the fake sandbox with real
   providers behind `ISpikeActivities` without touching the workflow. Spike 3 can
   add providers the same way.
2. **A real model is available**, through OpenRouter on `copilot-cli`. Spike 3 can
   exercise model-authored work from the start rather than discovering late that
   it cannot.
3. **R4 instability is real.** A cross-ticket review at epic scope will face the
   same instability, and the seeded defect makes it measurable — which is a reason
   to design the measurement deliberately rather than inherit it.
4. **F1 and code host protection are settled** as design requirements R2 and R3,
   so Spike 3 should not re-litigate them.
5. **Claude Code is deferred, not dropped.** The runtime stays pluggable; if an
   Anthropic credential appears, the second instance is already registered.
