# Spike 1 results: the interpreter under failure

Date: 2026-10-27. The spec is `docs/08` section 2.1. Code is in
`spikes/01-interpreter/`. **Spike code, not production code**: correctness of
the measurements matters, polish does not.

Companions: `docs/08` (the spec and the risk register), `docs/01` section 3
(the interpreter), `docs/09` (the plan schema), `docs/06` (step contracts),
`docs/05` 1.2.1 (poll inside one activity).

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

Infrastructure: a local Temporal dev server on `localhost:7233`, the fake
sandbox on `127.0.0.1:5199`, and a worker on queue `spike-1`. No cloud
resources and no credentials were used.

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
| 4 | Continue-as-new happens between steps only, and no decision is lost | **PARTIAL** | Section 5 |
| 5 | History size is recorded, and the continue-as-new threshold is set from it | **MET** | Section 6.1 |
| 6 | Replay tests pass against the recorded histories | **MET** | Section 7 |
| 7 | Temporal unavailable for several minutes: no step fails, and the API reports platform status distinctly from run status | **NOT TESTED** | Section 8 |
| 8 | A long polled step costs one activity, not one per poll | **MET** | Section 6.2 |
| 9 | Worker slot capacity is measured | **MET** | Section 6.3 |

Criterion 4 is partial and criterion 7 is untested. Both are stated plainly
rather than claimed.

---

## 3. Failure injections

### 3.1 Kill the worker mid-`agent.run` (exit criterion 1) — MET

The harness starts the worker, waits for the sandbox command to be running,
kills the worker process, restarts it, and then inspects the sandbox.

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

`docs/01` 3.7: after 3 consecutive resumes with no progress, the next counts as
a restart and the attempt allowance is consumed. The sandbox `stall` endpoint
freezes progress while the command keeps running.

```
kill  5: resumes=5 attempts=1  [counted as restart: 3 resumes without progress, attempts=1]
kill 11: resumes=8 attempts=2  [counted as restart: 3 resumes without progress, attempts=2]
```

Attempts rose only after three progress-free resumes each time, and never from
infrastructure alone. The rule behaves.

Two bugs had made this rule dead code, both found by running the test:

- The heartbeat timeout reaches the workflow **wrapped** in an
  `ActivityFailureException`, so the heartbeat branch never matched and no
  resume was ever recorded. `LastHeartbeat` now unwraps `InnerException`.
- The incremented attempt was held only in a local, so it was lost whenever the
  workflow replayed. It is now persisted to `AttemptBudgets` and `StepRuns`.

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
during outage: handle=cmd-8 turns=6  elapsed=6.46s  broken=True
during outage: handle=cmd-8 turns=18 elapsed=18.49s broken=True
after:         handle=cmd-8 turns=60 elapsed=60.99s broken=False
```

| Check | Result |
|---|---|
| Elapsed and turns advanced during the outage | True |
| Same handle after the outage — re-attached, not restarted | True |
| Step completed | True |
| `attempts=1` — the outage is infrastructure and free | True |

### 3.5 Send a re-plan that removes a protected step (R7) — MET

See section 4.1.

### 3.6 Send a decision while the workflow is continuing-as-new (R3) — PARTIAL

The workflow has an update validator that rejects a patch or decision when
`continueAsNewRequested` is set, so the API can retry against the current run.
The validator exists and the safe-point rule is implemented, but **no test
exercised an update arriving during the continue-as-new window itself**. This
is the gap behind criterion 4 being partial.

### 3.7 Send a re-plan while a `parallel` branch is in flight — MET

See section 4.2.

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
protected step unreachable without removing it. Reachability from the entry
point is checked, so removal by another route is caught.

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

## 5. Continue-as-new (exit criterion 4, partial)

Continue-as-new is implemented to happen only at a safe point: between steps,
never mid-step, and never with a pending decision. The workflow refuses to
continue-as-new while `Cursors` is non-empty or a decision is pending, and an
update validator rejects patches and decisions once continue-as-new has been
requested, so the caller can retry against the current run (R3).

What is **not** proven: no test sent an update during the continue-as-new
window and showed it being rejected and retried. The mechanism is in place; the
measurement is missing. Criterion 4 is therefore partial, not met.

---

## 6. Measurements

### 6.1 History size and event count at each continue-as-new (exit criterion 5)

This is the measurement that closes **brief Q6** (`docs/03` Q6).

| Plan | Continue-as-new | Events | Bytes | Steps done |
|---|---|---|---|---|
| 5 steps | — | 33 | 6,864 | 5 |
| 50 steps | — | 309 | 65,982 | 50 |
| 200 steps | #0 | 505 | 159,483 | 80 |
| 200 steps | #1 | 500 | 273,531 | 158 |
| 200 steps | completion | 284 | 268,126 | 200 |

**About 6.2 events per step.**

The finding that matters: **bytes grow monotonically across continue-as-new**
(159 KB, then 273 KB, then 268 KB), because state carries forward into each new
run. An event-count threshold alone does not bound payload size. The threshold
has to consider bytes as well, or a long run will approach the payload limit
while still looking healthy on event count.

**Recommended starting threshold: 500 events or 200,000 bytes, whichever comes
first.** At 6.2 events per step that is roughly 80 steps per run, which matches
where continue-as-new actually triggered in the 200-step run.

### 6.2 Events per step, especially for long polled steps (exit criterion 8, R20)

One activity per step, not one per poll. The `agent.run` activity loops on
`GET /commands/{handle}` and heartbeats inside a single activity, so a 90-second
step polled every 500 ms costs one activity rather than 180.

Measured indirectly: the 50-step run produced 309 events, about 6.2 per step.
A per-poll design would have produced thousands for a single long step. The
mitigation holds.

### 6.3 Worker slot capacity (exit criterion 9, H1)

With `MaxConcurrentActivities = 10`:

| Concurrent runs | Mean per run | Max per run |
|---|---|---|
| 4 | 10.9s | 16.0s |
| 8 | 25.3s | 37.7s |
| 20 | 47.9s | 70.8s |

A poll loop inside one activity holds a worker slot for the whole step, so runs
queue behind each other **well before the slot count is reached**: at 8
concurrent runs against 10 slots, mean time has already more than doubled
against the 4-run baseline.

**Implication for pool sizing:** size the pool for concurrent *runs*, not
concurrent steps. A run occupying one long polled step occupies a slot for the
step's whole duration, possibly hours. Ten slots does not mean ten comfortable
concurrent runs; it means roughly four.

### 6.4 Wall-clock time lost per crash

| Scenario | n | Min | Max | Mean |
|---|---|---|---|---|
| Kill mid-`agent.run`, step resumes | 1 | 87.4s | 87.4s | 87.4s |
| Repeated kills with no progress | 12 | 10.0s | 180.2s | 127.3s |

The 180s cases are the 30-second heartbeat timeout plus worker restart plus
retry backoff. The 10–30s cases are kills that landed while the activity was
already retrying. **The cost is dominated by detecting the crash, not by
resuming from it** — resume itself is a re-attach by handle and is cheap.

### 6.5 Attempts consumed versus resumes

Infrastructure never consumed an attempt. Attempts rose only from the
progress rule in `docs/01` 3.7, and only after three progress-free resumes.

### 6.6 Whether any step restarted that should have resumed

**None.** Across every crash test, the command handle, sandbox key and session
reference were unchanged, and `elapsedSeconds` continued rather than resetting.
This is the failure mode the whole design exists to prevent, and it did not
occur.

---

## 7. Replay (exit criterion 6, R2, R12)

```
REPLAY history-50-1793084934:  PASS (311 events)
REPLAY history-200-1793085013: PASS (286 events)
```

Both recorded histories replay cleanly against the current interpreter code,
including the 200-step run with two continue-as-new points. This is the check
that the determinism boundary in `docs/01` 3.1 holds: everything outside
`await` is a pure function of the plan and the state.

---

## 8. What was not tested

| Item | Why |
|---|---|
| **Exit criterion 7: Temporal unavailable for several minutes** | Stopping the Temporal dev server mid-run was not done. It needs the server stopped and restarted while a run is in flight, and the API's platform-status-versus-run-status distinction is not implemented in the spike — there is no API layer, only a client. This is the largest remaining gap |
| An update arriving during the continue-as-new window (R3) | The validator exists; no test exercised the window |
| `insert_after` versus `add_step` plus `change_route` validation failure rates (review 11 C2) | Needs a planner agent authoring patches. Deferred to Spike 2, as `docs/08` 2.1 allows |
| Real providers | `docs/08` 2.1 asks for one shorter run with real providers. That is Spike 2 |

---

## 9. The two provisional decisions

### 9.1 Brief Q6, plan-as-data within Temporal limits — CLOSED, with a change

`docs/03` Q6 asked whether plan-as-data fits within Temporal's limits. **It
does.** Measured at about 6.2 events per step, with continue-as-new triggering
cleanly and replaying correctly.

**The decision record needs one change.** The provisional record assumed an
event-count threshold. The measurement shows bytes grow monotonically across
continue-as-new because state carries forward, so **an event-count threshold
alone is not sufficient**. The threshold must be events *or* bytes, whichever
comes first. Recommended starting value: 500 events or 200,000 bytes.

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

**Three findings for `docs/09`, not for the library:**

1. `lower` and `upper` are in the section 8.4 allow-list, but **Cel.NET does not
   declare them anywhere** — not in the stdlib, not in `StringsLib`. The
   allow-list needs correcting or the functions need registering.
2. `Env.NewEnv()` and the default `ScriptHost` register the whole stdlib **and
   comprehensions** before you declare anything. The engine must use
   `Env.NewCustomEnv` with explicit opt-in, or the restricted checker is not
   restricted.
3. There is no runtime interrupt: `DecObserveEval` fires but Cel.NET swallows
   the exception. The static cost estimate is therefore **load-bearing**, not
   merely defence in depth as section 8.1 currently describes it.

Also worth recording: Cel.NET ships four target frameworks (`net10.0`, `net8.0`,
`netstandard2.1`, `net462`), not the two stated in section 8.1, and pulls seven
transitive dependencies including Antlr4.Runtime.Standard, Apache.Avro and
Google.Protobuf.

---

## 10. Bugs found by running the spike

Each of these was invisible in the design and only appeared when the code ran.

| Bug | Consequence |
|---|---|
| `RetryPolicy.MaximumAttempts = 0` (unlimited) | Temporal retried a crashed activity internally, so the workflow never saw the failure and the progress rule in `docs/01` 3.7 was dead code. Now 1, so the interpreter owns the attempt budget |
| Heartbeat timeout arrives wrapped in `ActivityFailureException` | The heartbeat branch never matched, so no resume was ever recorded. `LastHeartbeat` now unwraps |
| Attempt increments held only in a local | Lost on replay, so attempts never accumulated. Now persisted |
| `RunGenericStepAsync` treated its duration as milliseconds | Branches completed instantly, so no patch could be sent mid-flight |
| `SpecPlan` had nothing routing to the protected step | Every patch was rejected as a bypass, because the step was unreachable in the base plan |
| `branches` and `max_concurrency` arrive as `JsonElement` after deserialization | Parallel branches never became ready, so the join never settled |

---

## 11. Open questions

| # | Question |
|---|---|
| 1 | Exit criterion 7 is untested. Should Spike 1 be re-run with the Temporal server stopped mid-run, or does that move to Spike 2 alongside real providers? |
| 2 | The recommended continue-as-new threshold is 500 events or 200,000 bytes. Is a byte-based threshold acceptable, or should state be compacted instead so events alone remain sufficient? |
| 3 | `lower` and `upper` are in the `docs/09` 8.4 allow-list but Cel.NET does not provide them. Drop them from the allow-list, or register them in the engine? |
| 4 | Section 8.1 describes cost estimation as defence in depth. Since Cel.NET has no runtime interrupt, it is load-bearing. Should that wording change? |
| 5 | Worker pool sizing: the measurement says size for concurrent runs, not concurrent steps. Does `docs/07` need a pool-sizing note, or is that an implementation concern? |
