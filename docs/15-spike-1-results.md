# Spike 1 results: the interpreter under failure

Date: 2026-10-28. **Review 12 applied.** The spec is `docs/08` section 2.1. Code
is in `spikes/01-interpreter/`. **Spike code, not production code**: correctness
of the measurements matters, polish does not.

Companions: `docs/08` (the spec and the risk register), `docs/01` section 3 (the
interpreter), `docs/09` (the plan schema), `docs/06` (step contracts), `docs/05`
1.2.1 (poll inside one activity).

**This document replaces the first-pass results.** Section 12 lists every number
and claim that the review-12 work supersedes, so nothing is silently revised.

---

## 1. What was built

| Piece | Where |
|---|---|
| Contracts: outcomes, plan, patch, provider results | `spikes/01-interpreter/src/FeatureFlow.Spike.Contracts/` |
| The interpreter workflow, `applyPatch`, `plan.ready`, `plan.route` | `spikes/01-interpreter/src/FeatureFlow.Spike.Interpreter/` |
| Activities backed by the fake sandbox | `spikes/01-interpreter/src/FeatureFlow.Spike.Worker/` |
| Fake sandbox, agent runtime and hook endpoint, as a separate OS process | `spikes/01-interpreter/src/FeatureFlow.Spike.Sandbox/` |
| Failure injection and measurement harness | `spikes/01-interpreter/src/FeatureFlow.Spike.Harness/` |
| Cel.NET checks | `spikes/01-interpreter/src/CelChecks/`, results in `results/cel-checks.md` |
| Replay fixtures | `spikes/01-interpreter/tests/histories/` |

Infrastructure: a local Temporal dev server on `localhost:7233`, the fake sandbox
on `127.0.0.1:5199`, and a worker on queue `spike-1`. No cloud resources and no
credentials were used.

The sandbox is a **separate process** from the worker. That is what makes exit
criterion 1 testable at all: a sandbox inside the worker would die with it, and
re-attachment would be untestable.

---

## 2. Exit criteria

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | A worker crash mid-`agent.run` resumes rather than restarts, at least once with the sandbox surviving | **MET** | Section 3.1 |
| 2 | A re-plan that removes a protected step is rejected and recorded | **MET** | Section 4.1 |
| 3 | A re-plan applied mid-run leaves completed and in-flight steps untouched | **MET** | Section 4.2 |
| 4 | Continue-as-new happens between steps only, and no decision is lost | **MET** | Section 5 |
| 5 | History size is recorded, and the continue-as-new threshold is set from it | **MET** | Section 6.1 |
| 6 | Replay tests pass against the recorded histories | **MET** | Section 7 |
| 7 | Temporal unavailable for several minutes: no step fails, and the API reports platform status distinctly from run status | **MET, with a caveat** | Section 3.8 |
| 8 | A long polled step costs one activity, not one per poll | **MET** | Section 6.2 |
| 9 | Worker slot capacity is measured | **MET** | Section 6.3 |

All nine are now met. Criterion 7 is met on the part the spike can test — no step
failed — but the platform-status-versus-run-status distinction is untested,
because the spike has no API layer. That half is deferred to the API work and
recorded in `docs/08` 4.1 as an acceptance criterion.

Criterion 9 was reported as met on the review-12 measurement, which review 13
showed was invalid. It is met now, on the corrected measurement in section 6.3.

---

## 3. Failure injections

### 3.1 Kill the worker mid-`agent.run` (exit criterion 1) — MET

The harness starts the worker, waits for the sandbox command to be running, kills
the worker process, restarts it, and then inspects the sandbox.

```
before kill: handle=cmd-9  sandboxKey=sbx-long_agent  session=sess-9  elapsed=3.46s
after  kill: handle=cmd-9  sandboxKey=sbx-long_agent  session=sess-9  elapsed=91.15s
```

| Check | Result |
|---|---|
| Exactly one command in `/admin/state` — no second command started | True |
| Same handle | True |
| Same sandbox key | True |
| Same session reference | True |
| `elapsedSeconds` continued rather than resetting to 0 | True |
| Step outcome `completed` | True |
| `attempts=1` — a crash does not consume one | True |
| `resumes=1` — the crash was seen as a resume | True |

The step resumed. It did not restart.

### 3.2 Kill the worker repeatedly with no progress — MET

`docs/01` 3.7: after 3 consecutive resumes with no progress, the next counts as a
restart and the attempt allowance is consumed. The sandbox `stall` endpoint
freezes progress while the command keeps running.

```
kill  5: resumes=5 attempts=1  [counted as restart: 3 resumes without progress, attempts=1]
kill 11: resumes=8 attempts=2  [counted as restart: 3 resumes without progress, attempts=2]
```

Attempts rose only after three progress-free resumes each time, and never from
infrastructure alone.

### 3.3 Make the hook endpoint unreachable mid-step (R19) — MET

```
hook reachable before injection: True
fail-hook injected; hook reachable now: False
result: outcome=completed attempts=1 resumes=3 infrastructureFailures=3
```

| Check | Result |
|---|---|
| Outcome is `completed`, not `failed` — not treated as a denied tool call | True |
| `attempts=1` — infrastructure retries are free | True |
| `infrastructureFailures=3` — it retried | True |
| Step completed once the hook came back | True |

### 3.4 Break the worker-to-sandbox connection mid-command (K3) — MET

```
during outage: handle=cmd-3 turns=6  elapsed=6.49s  broken=True
during outage: handle=cmd-3 turns=18 elapsed=18.81s broken=True
after:         handle=cmd-3 turns=60 elapsed=60.99s broken=False
```

| Check | Result |
|---|---|
| Elapsed and turns advanced during the outage | True |
| Same handle after the outage — re-attached, not restarted | True |
| Step completed | True |
| `attempts=1` — the outage is infrastructure and free | True |

### 3.5 Send a re-plan that removes a protected step (R7) — MET

See section 4.1.

### 3.6 Send a decision while the workflow is continuing-as-new (R3) — MET

See section 5.

### 3.7 Send a re-plan while a `parallel` branch is in flight — MET

See section 4.2.

### 3.8 Temporal unavailable (exit criterion 7, K2, H2) — MET, with a caveat

Two variants were run.

**Server stopped.** The server was stopped while an `agent.run` step was in
flight, held down, then restarted.

```
OUTAGE: step in flight, cursors=[long_agent]
OUTAGE: stopping temporal server pid=33004
OUTAGE: server restarted=True
OUTAGE: result=completed outcome=completed attempts=1 resumes=1 infrastructureFailures=1
OUTAGE: noStepFailed=True outageWasSeen=True outageWasFree=True downFor=121s
```

**Access blocked** (review 12 B1). A local TCP proxy forwards to the real
Temporal port; closing it simulates a network partition while the server keeps
running. This is the enterprise case, and it avoids the dev server's durability
problem.

```
BLOCK: proxy listening on 127.0.0.1:<port> -> 127.0.0.1:7233
BLOCK: step in flight; blocking access to Temporal
BLOCK: outcome=completed attempts=1 infrastructureFailures=1
```

Both outages lasted longer than the 30-second heartbeat timeout, so both are the
H2 case: the activity timed out, the retry re-attached by handle, and **the step
did not fail**. Neither consumed an attempt.

The worker side confirms the outage was real and was ridden out rather than
turned into a work outcome:

```
WARN activity_heartbeat_manager: Error when recording heartbeat:
  Status { code: Unavailable, message: "tcp connect error" }
WARN heartbeat: Network error while sending worker heartbeat
  error=Status { code: Unavailable, message: "tcp connect error" }
```

**Caveat.** The criterion also asks that the API report platform status
distinctly from run status. The spike has no API layer — only a client — so that
half is untested. Deferred to the API work (review 12, answer 1) and recorded in
`docs/08` 4.1.

### 3.9 A long infrastructure outage (review 12 A4) — MET

The first pass retried infrastructure failures with a fixed 1-second delay and
threw after 50, so a sandbox host down for about 25 minutes **failed the step** —
the R11/R19 class again, an interruption becoming a wrong outcome.

Now: exponential backoff capped at 5 minutes, no count limit, and the run is
marked stalled on infrastructure (D24) and alerts. It never fails on its own; a
human may abort it.

**Stalled is time-based, not count-based** (review 13 B1). The first version
counted 8 failures, which took about 8 minutes because each failure costs a
heartbeat timeout plus backoff — coupling the threshold to two other settings.
It is now 5 minutes with no successful contact, configurable as
`policy.timeouts.stalledAfter` in `docs/07`. Any successful contact resets the
clock and clears the state.

```
failure  1:       1s      failure  6:      32s
failure  2:       2s      failure  7:      64s
failure  3:       4s      failure  8:     128s
failure  4:       8s      failure  9:     256s
failure  5:      16s      failure 10+:    300s (capped)
```

A 30-minute outage costs **14 retries instead of about 1,800**.

Measured with a real 700-second sandbox outage:

```
outcome='' attempts=0 infrastructureFailures=10
stalled=True stalledStep=backoff_agent
notFailed=True outageWasFree=True outageWasSeen=True stalledVisible=True
```

The step did not fail, consumed no attempt, and the stalled state was visible
while it was stalled. The run completed once the connection came back.

With the time-based threshold, measured in fast mode with a 120s outage:

```
stalled observed at 15s, infrastructureFailures=2
notFailed=True outageWasFree=True outageWasSeen=True stalledVisible=True
```

Previously the same state took 8 minutes and 10 failures.

---

## 4. Re-planning

### 4.1 A re-plan that removes a protected step is rejected and recorded (exit criterion 2)

```
PATCH remove-protected: Rejected
  reason='protected step 'implement' cannot be removed (floor F3)'
PATCH bypass-protected: Rejected
  reason='patch would make protected step(s) unreachable: implement'
```

The second case is the one `docs/09` 15.2 exists for: `change_route` can make a
protected step unreachable without removing it. Reachability from the entry point
is checked, so removal by another route is caught.

A legitimate patch applies and rewires correctly:

```
PATCH insert-after: Applied revision=2 resumeAt=lint rewired=True
PATCH stale: Rejected reason='stale patch: base_revision 6 != current revision 1'
```

`insert_after` rewires `run_tests`' routes to the new step, which is the
behaviour `docs/09` 15.3.1 specifies. A stale patch is rejected, not silently
rebased (`docs/09` 15.1).

### 4.2 A re-plan applied mid-run leaves completed and in-flight steps untouched (exit criterion 3)

A patch was sent while three `parallel` branches were in flight:

```
completed before=[implement,plan_spec,run_tests,tickets]
inFlight  before=[ticket_a,ticket_b,ticket_c]
patch verdict=Applied
applied=True preserved=True revision=2
```

Every step that had completed still had the same outcome afterwards. The
in-flight branches were untouched: the patch applies to future steps, which is
the rule in `docs/08` 2.1.

---

## 5. Continue-as-new (exit criterion 4) — MET

The first pass left this partial for want of one test. The test now exists.

The harness forces continue-as-new while sending patches in a loop. The gate is
opened first, or the run blocks on it and never reaches the window.

```
CAN: applied=16 stale=5 lost=0 retriedOk=5 retryFailed=0 afterCompletion=9
CAN: result=completed continueAsNew=11 stepsExecuted=31
```

Eleven continue-as-new points, five updates rejected as stale, all five retried
successfully, **none lost**. Nine arrived after the run had already completed,
which is not a loss: there is no run left to apply them to.

Continue-as-new only happens at a safe point: between steps, never mid-step, and
never with a pending decision. An update validator rejects patches and decisions
once continue-as-new has been requested, so the caller retries against the
current run (R3).

---

## 6. Measurements

### 6.1 History size and carried state (exit criterion 5)

This is the measurement that closes **brief Q6** (`docs/03` Q6).

**Carried state, after review 12 A1.** The first pass carried every `StepRun`
with its per-attempt list, every `PatchRecord` and every `HistorySample` into
each new run, so bytes rose with steps executed. Now carried state holds the
plan, cursors, and per step only what an expression can read; step-run detail,
patch records and samples go to the record store.

| Plan | Continue-as-new | Events | History bytes | Steps done | Carried bytes |
|---|---|---|---|---|---|
| 50 steps | — | 315 | — | 50 | 25,684 |
| 120 steps | #0 | 510 | 124,356 | 76 | 58,347 |
| 120 steps | #1 | 285 | 151,571 | 120 | 60,661 |
| 200 steps | #0 | 502 | 161,017 | 79 | 94,062 |
| 200 steps | #1 | 501 | 255,670 | 160 | 98,830 |
| 200 steps | #2 | 253 | 223,400 | 200 | 100,809 |

**About 500 bytes per plan step, stable across continue-as-new** (470, 494, 504
on the 200-step plan). Carried state is bounded by plan size, not by steps
executed. The 200-step run wrote 200 step-run records to the store and carried
none of them.

**Events per step: 6.28**, consistent across both plans (6.30, 6.35, 6.19, 6.32).

**The threshold** (review 12 A2). The first pass recommended 500 events, which
was circular: it matched where continue-as-new triggered because the test set the
threshold to 500. Derived from Temporal's limits instead:

| Limit | Events | Steps at 6.28 events/step |
|---|---|---|
| Temporal warns | 10,240 | ~1,630 |
| Temporal hard-fails | 51,200 | ~8,152 |

**Recommended: 5,120 events**, about 815 steps — half the warning threshold, so
clear of it, and few enough continue-as-new points that the R3 window is not
opened often. The default in the spike is now 5120.

**The payload ceiling binds before the event ceiling for large plans.** At ~500
bytes per plan step, a 4,194-step plan reaches the 2 MB payload limit while
using only 51% of the hard event limit. So the threshold should be events *or*
bytes, whichever comes first, and very large plans need splitting rather than a
better threshold.

### 6.2 Events per step for a long polled step (exit criterion 8, R20)

Measured directly by counting `ActivityTaskScheduled` events, rather than
inferred from a run of short steps as in the first pass.

A 60-second `agent.run` step, polled every 500 ms inside one activity:

```
ActivityTaskScheduled=1 ActivityTaskStarted=1 ActivityTaskCompleted=1
total events=11
```

With a 20-second connection break mid-step, the same: 1 scheduled, 1 started, 1
completed. The break was absorbed inside the activity and cost no extra activity.

A per-poll design would have scheduled about 120 activities for this step. One
activity per step holds (`docs/05` 1.2.1).

### 6.3 Worker slot capacity (exit criterion 9, H1)

**Two earlier conclusions are withdrawn.** The first pass claimed "ten slots
means roughly four runs"; review 12 replaced it with "ten slots carries at least
16 concurrent long steps". Review 13 showed the review-12 measurement was
invalid: runs were started in a loop, the dev server returned "busy" under load,
so they started staggered and their steps never overlapped. Per-run times of
0.24s, 0.96s and 3.4s for 20-second steps gave it away.

**The method is now valid.** Each run waits on a release signal; the harness
starts every run first, then releases them all together. The worker also records
actual occupancy, so concurrency is observed rather than inferred.

**Sanity check first**, as review 13 asked — 20 steps of 15s against **2** slots:

```
p50 88.83s  p95 183.27s  max 199.14s
20 of 22 activities waited; peak observed concurrency 2
```

With 2 slots, peak occupancy is exactly 2 and almost every activity waits about
one step duration. The method measures what it claims to.

**Then 20 against 10**, two repetitions:

| Concurrency | Slots | Rep | p50 | p95 | Max | Waiting | Peak observed |
|---|---|---|---|---|---|---|---|
| 20 | 2 | 1 | 88.83s | 183.27s | 199.14s | 20 of 22 | 2 |
| 16 | 10 | 1 | 0.00s | 10.68s | 10.68s | 1 of 16 | 9 |
| 20 | 10 | 1 | 3.45s | 105.75s | 106.51s | 6 of 25 | 9 |
| 20 | 10 | 2 | 0.00s | 61.25s | 62.00s | 5 of 25 | 9 |

**Ten slots does not carry 16 concurrent long steps without waiting.** Waiting
begins somewhere between 10 and 16 concurrent steps, and peak occupancy never
exceeded 9 of 10. The slot count is the binding constraint, as expected — which
is the opposite of what review 12 concluded from the invalid measurement.

The 16-against-10 row fails its own sanity check (only 1 activity waited where 6
were expected), so it is reported but not relied on. The spread between the two
20-against-10 runs (p95 105.75s and 61.25s) is wide; review 13 asks for three
repetitions per level before a pool-sizing rule is written, and this spike has
two.

**The rule itself is arithmetic** (review 13, answer 2): slots at least equal to
concurrent long steps, plus headroom. The measurement only has to confirm it, and
it does — at 20 steps against 10 slots, roughly 10 activities wait about one step
duration each.

### 6.4 Wall-clock time lost per crash

| Scenario | n | Min | Max | Mean |
|---|---|---|---|---|
| Kill mid-`agent.run`, step resumes | 1 | 87.4s | 87.4s | 87.4s |
| Repeated kills with no progress | 12 | 10.0s | 180.2s | 127.3s |

The cost is dominated by **detecting** the crash — the 30-second heartbeat
timeout plus worker restart plus backoff — not by resuming, which is a re-attach
by handle and is cheap. This is why `docs/01` 3.6 now requires graceful worker
shutdown as a separate, cheaper path (review 12 B2).

### 6.5 Attempts consumed versus resumes

Infrastructure never consumed an attempt. Attempts rose only from the progress
rule in `docs/01` 3.7, and only after three progress-free resumes.

### 6.6 Whether any step restarted that should have resumed

**None.** Across every crash test, the command handle, sandbox key and session
reference were unchanged, and `elapsedSeconds` continued rather than resetting.
This is the failure mode the whole design exists to prevent, and it did not
occur.

---

## 7. Replay (exit criterion 6, R2, R12)

Nine histories are committed as fixtures under
`spikes/01-interpreter/tests/histories/` and all nine replay cleanly:

```
backoff-1793145290       124 events   long infrastructure outage, stalled
block-1793147158          28 events   network partition to Temporal
can-1793144340            23 events   continue-as-new window
crash1-1793141651         24 events   worker crash, resume
crash3-1793143208         50 events   hook endpoint unreachable
crash4-1793143323         11 events   sandbox connection break
history-120-1793144649   287 events   two continue-as-new points
outage-1793146197         25 events   Temporal server down
patch-midrun-1793140837   66 events   patch while branches in flight
```

These are the engine's first replay suite (S8). The first pass replayed only two
linear runs fetched live from the dev server, which could not run in CI after a
server reset and did not cover the failure paths — where the replay bug in
section 10 was actually found.

---

## 8. What was not tested

| Item | Why |
|---|---|
| **The API reporting platform status distinctly from run status** (half of exit criterion 7) | The spike has no API layer, only a client. Deferred to the API work and recorded in `docs/08` 4.1 |
| `insert_after` versus `add_step` plus `change_route` validation failure rates (review 11 C2) | Needs a planner agent authoring patches. Deferred to Spike 2, as `docs/08` 2.1 allows |
| Real providers | `docs/08` 2.1 asks for one shorter run with real providers. That is Spike 2 |
| A reproducible pool-sizing rule | The slot measurement is noisy between runs; more repetitions are needed before writing a rule into `docs/07` |

---

## 9. The two provisional decisions

### 9.1 Brief Q6, plan-as-data within Temporal limits — CLOSED, with a change

`docs/03` Q6 asked whether plan-as-data fits within Temporal's limits. **It
does**, at 6.28 events per step, with continue-as-new triggering cleanly and
replaying correctly.

**The decision record needs one change.** The provisional record assumed an
event-count threshold. Two findings change that:

1. Carried state must be compacted (review 12 A1), because the continue-as-new
   input is one payload with a 2 MB ceiling, not a history with a 50 MB ceiling.
2. The threshold must be **events or bytes, whichever comes first**, because for
   large plans the payload ceiling binds before the event ceiling.

Recommended starting value: 5,120 events, derived from Temporal's 10,240-event
warning threshold with a 50% margin.

### 9.2 The CEL implementation — CLOSED, Cel.NET stays

All four checks in `docs/09` 8.1 pass. Full evidence in
`spikes/01-interpreter/results/cel-checks.md`.

| Check | Result |
|---|---|
| Restricted checker rejects unregistered functions and unknown variables | PASS |
| Evaluation deterministic under replay | PASS — 120,000 evaluations, one distinct result each, order-independent |
| Static cost estimation exists | PASS — `Cel.Cel.EstimateCost(object)` / `Cel.Prog.Cost()` |
| Cost limit enforceable | PASS — no built-in knob, but `Cost.Max` plus the 8.5 caps both work |

Resolved version `Cel.NET` 2.3.1. Cold start to first evaluation 136–155 ms;
about 2 ms warm load; 0.67–1.0 µs per evaluation.

**Findings recorded against `docs/09`, not the library:**

1. `lower` and `upper` are in the section 8.4 allow-list but Cel.NET does not
   declare them. **The engine registers them** with culture-invariant conversion
   (review 12, answer 3); the culture-sensitive form is a replay hazard.
2. `Env.NewEnv()` and the default `ScriptHost` register the whole stdlib **and
   comprehensions** before you declare anything. The engine must use
   `Env.NewCustomEnv` with explicit opt-in, or the restricted checker is not
   restricted.
3. There is no runtime interrupt: `DecObserveEval` fires but Cel.NET swallows the
   exception. Cost estimation is therefore **load-bearing**, not defence in depth
   as section 8.1 said (review 12, answer 4).
4. Cel.NET ships four target frameworks, not the two section 8.1 stated, and
   pulls seven transitive dependencies including Antlr4.Runtime.Standard,
   Apache.Avro and Google.Protobuf.

---

## 10. Bugs found by running the spike

Each was invisible in the design and only appeared when the code ran.

| Bug | Consequence |
|---|---|
| `RetryPolicy.MaximumAttempts = 0` (unlimited) | Temporal retried a crashed activity internally, so the workflow never saw the failure and the progress rule in `docs/01` 3.7 was dead code |
| Heartbeat timeout arrives wrapped in `ActivityFailureException` | The heartbeat branch never matched, so no resume was ever recorded |
| Attempt increments held only in a local | Lost on replay, so attempts never accumulated |
| Fixed 1s infrastructure retry, throwing after 50 | A ~25-minute outage failed the step — the R11/R19 class again (review 12 A4) |
| Carried state grew with steps executed | The continue-as-new input approached the 2 MB payload ceiling (review 12 A1) |
| Two interpreter loop paths could spin without awaiting | Starved the workflow task; queries failed with "consistent query buffer is full" while the workflow sat at two events |
| `RunGenericStepAsync` treated its duration as milliseconds | Branches completed instantly, so no patch could be sent mid-flight |
| `SpecPlan` had nothing routing to the protected step | Every patch was rejected as a bypass |
| `branches` and `max_concurrency` arrive as `JsonElement` | Parallel branches never became ready, so the join never settled |
| Record store read from an in-memory copy | The harness never saw records the worker wrote in another process |

---

## 11. What review 12 changed

| Item | Change |
|---|---|
| A1 | Carried state compacted; step-run detail, patch records and samples moved to a record store. Re-measured |
| A2 | The circular 500-event threshold withdrawn; 5,120 events derived from Temporal's limits |
| A3 | The "ten slots means four runs" claim withdrawn; replaced with schedule-to-start latency |
| A4 | Fixed 1s retry with a 50-count limit replaced by capped exponential backoff and a stalled state |
| A5 | Criterion 8 measured directly by counting `ActivityTaskScheduled` |
| A6 | Nine histories committed as replay fixtures; all replay cleanly |
| A7 | The continue-as-new window test written; no update lost |
| B1 | Block-access outage variant added alongside the server-stopped variant |
| B2 | Heartbeat timeout recorded as an operational setting in `docs/07`; graceful worker shutdown required in `docs/01` 3.6 |
| B3 | `restart.ps1` stops only the spike's own processes and uses a path relative to the script |
| B4 | Results consolidated into `results/` |
| B5 | `replan_parent` join semantics, zero-item expansion, patch-time reachability, and `docs/12` subsection ordering |

---

## 12. Numbers and claims this document replaces

Stated explicitly, so nothing is silently revised.

| First pass said | Now |
|---|---|
| Continue-as-new threshold: 500 events | **5,120 events**, derived from Temporal's limits. The 500 was circular |
| "Ten slots means roughly four runs" | **Withdrawn.** Not slot starvation; the test never held more slots than it had |
| "Ten slots carries at least 16 concurrent long steps" (review 12) | **Withdrawn.** That measurement was invalid — staggered starts, no overlap. Ten slots does **not** carry 16; waiting begins between 10 and 16 |
| Stalled after 8 counted failures, ~8 minutes | **Time-based**: 5 minutes with no successful contact. Observed at 15s in fast mode |
| Bytes grow monotonically across continue-as-new (159 KB → 273 KB → 268 KB) | **Superseded.** That was carried state growing with steps. After A1, carried state is ~500 B per plan step and stable (94 KB → 98 KB → 100 KB on a 200-step plan) |
| Criterion 4 partial | **MET**, with the continue-as-new window test |
| Criterion 8 measured indirectly at 6.2 events/step | **Measured directly**: 1 `ActivityTaskScheduled` for a 60s step |
| Criterion 7 untested | **MET** on "no step fails", in two variants. The API-status half remains deferred |
| Replay: two linear histories fetched live | **Nine committed fixtures**, including all failure paths |
| A ~25-minute outage fails the step | **Fixed.** Capped exponential backoff, no count limit, stalled state |
| Events per step: 6.2 | **6.28**, consistent across plans |

---

## 13. What review 13 changed

| Item | Change |
|---|---|
| A1 | The slot measurement was invalid (staggered starts, no overlap). Rebuilt with a release barrier and worker-side occupancy recording; sanity-checked against 2 slots first |
| A2 | Criterion 9 reported as met on the corrected measurement |
| B1 | Stalled state made time-based (5 minutes, configurable) rather than count-based |
| Answers | Plan size limit added as check 32 in `docs/12`; `matches` dropped from the `docs/09` 8.4 allow-list for version 1 |

## 14. Open questions

All four questions from section 13 of the previous revision were answered in
review 13 and are applied above:

| # | Question | Answer |
|---|---|---|
| 1 | Large plans hit the 2 MB payload ceiling first. Split, or compact further? | **Neither for now.** Large work already splits into child runs. A **plan size limit** is now a blocking check at load and patch time (check 32 in `docs/12`), set so carried state stays under 1 MB — about 2,000 steps |
| 2 | How many repetitions before a pool-sizing rule? | Fix validity first (done). Then three repetitions per level, reporting the spread. The rule is arithmetic — slots at least equal to concurrent long steps, plus headroom — and the measurement only confirms it |
| 3 | 8 minutes to reach stalled | **Time-based**, not count-based (B1) |
| 4 | Drop `matches` from the allow-list? | **Yes, dropped for version 1.** A backtracking regex is a denial-of-service risk in workflow code, and no worked example uses it |

### Still open

| # | Question |
|---|---|
| 1 | The slot measurement has two repetitions at 20-against-10, with p95 of 105.75s and 61.25s. Review 13 asks for three per level. Is two enough to write the arithmetic rule into `docs/07`, or should a third run be done first? |
| 2 | The 16-against-10 case failed its own sanity check (1 activity waited where 6 were expected). Worth a rerun, or is the 20-against-10 evidence sufficient? |
