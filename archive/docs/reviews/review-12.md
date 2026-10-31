# Review 12: Spike 1 first results

Date: 2026-10-27. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/15-spike-1-results.md` and `spikes/01-interpreter/` at commit 8bcb491.

## Summary

**Strong first pass. Not yet closed.** The crash, resume, hook-outage, sandbox-break and Temporal-outage results are convincing, and the honesty is right: criteria 4 and 7 are marked partial rather than claimed, and section 10 lists the bugs that running the code found. Those bugs (retry policy hiding failures, the heartbeat exception being wrapped, attempts lost on replay) are exactly why the spike exists.

Three conclusions do not follow from the evidence, and one mechanism repeats the prototype's failure class. Fix those, then Spike 1 can close.

**Decided by the product owner, 2026-10-28:** Spike 1 stays open until A1-A7 are done. Update `docs/15` with the new results, and say which earlier numbers and claims they replace.

## A. Must fix before Spike 1 closes

| # | Issue | Change |
|---|---|---|
| A1 | **Carried state grows with steps executed.** `InterpreterState` carries every `StepRun` (with its per-attempt list, handles and outputs), every `PatchRecord` and every `HistorySample` into each new run. That is why bytes rise across continue-as-new (159 KB, 273 KB, 268 KB). The continue-as-new input is **one payload**, so its hard ceiling is the 2 MB payload limit, not the 50 MB history limit. At the measured growth (roughly 1.3 KB per step, an estimate from history bytes), a run reaches that ceiling after somewhere around 1,500 steps, and at that point continue-as-new cannot help, because the carried state alone is too big. `docs/01` 3.5 and DP5 say workflow state is compact and references only | **Compact the carried state.** Carry the plan, cursors, attempt budgets for steps that can still run, and per step only what expressions can read (`outcome`, `outputs`, `choice`). Step-run detail, patch records and samples go to the record store. Re-measure: **the continue-as-new input size must be bounded by plan size, not by steps executed**. Report it per continue-as-new |
| A2 | **The threshold recommendation is circular.** "500 events ... matches where continue-as-new actually triggered in the 200-step run": it triggered there because the test set the threshold to 500 | Derive the threshold from Temporal's limits (warnings at 10,240 events or 10 MB, hard limits at 51,200 events or 50 MB) with a margin, using the measured cost per step. After A1, events or bytes, whichever comes first, is a reasonable safety net. Expect a threshold in the low thousands of events, not 500: every continue-as-new opens an R3 window and costs a workflow task |
| A3 | **The slot conclusion does not follow.** The slot test runs `Linear(3)` per run, one activity at a time, so 8 runs never hold more than 8 of 10 slots. Slot starvation cannot explain the slowdown, and the spread in `slots-8.json` (two runs at 5.5 s, the rest at 25-38 s) points at contention elsewhere: the single fake sandbox process, the dev server, workflow task slots or CPU. "Ten slots means roughly four runs" is unsupported | Withdraw that claim until it is measured. Measure **schedule-to-start latency** per activity from the histories (`ActivityTaskScheduled` to `ActivityTaskStarted`), which is slot waiting by definition, and record worker slot usage. Then run more concurrent long steps than slots (for example 20 against 10) and show that schedule-to-start grows by about one step duration. Identify what caused the slowdown in the current test |
| A4 | **A long infrastructure outage becomes a failure.** The interpreter retries infrastructure failures with a fixed 1-second delay and **throws after 50**. With `MaximumAttempts = 1`, a sandbox host down for about 25 minutes (50 × (30 s heartbeat + 1 s)) fails the step, which is the R11/R19 class again: an interruption becomes a wrong outcome. A fixed 1-second retry against an immediately refusing endpoint also adds about 6 events per retry, the R20 problem in another form | Exponential backoff with a cap (for example up to 5 minutes), **no count limit**. Past a threshold the run is marked **stalled on infrastructure** (stuck-run detection, D24) and alerts, but never fails on its own. A human may abort it. Add a test: sandbox unreachable for 30 minutes; the step does not fail, history growth stays small, and the stalled state is visible |
| A5 | **Criterion 8 is measured indirectly** (6.2 events per step on a run of mostly short steps) | Measure it directly: count `ActivityTaskScheduled` events for the long polled step in the crash and K3 histories. It should be one, plus one per re-attach |
| A6 | **Replay tests replay histories fetched live from the dev server, against the code that produced them.** They are not kept, so they cannot run in CI after a server reset, and the replayed runs are the linear ones. The replay bug section 10 found (attempts lost on replay) was in the failure path | Commit the histories as fixtures: crash, repeated no-progress kills, hook outage, sandbox break, Temporal outage, patch mid-flight, continue-as-new. Replay all of them. These become the engine's first replay suite (S8) |
| A7 | **Criterion 4 is partial for want of one test** (R3: an update during the continue-as-new window) | Write it: force continue-as-new while sending decisions and patches in a loop; show each is either applied or rejected as stale and then retried against the new run, and none is lost. This is in Spike 1 scope and cheap |

## B. Should fix

| # | Issue | Change |
|---|---|---|
| B1 | The outage test kills the dev server, and the dev server's SQLite file does not always survive that | Also run the **block access** variant `docs/08` allows (drop traffic to port 7233 for several minutes, for example through a local proxy the harness controls). It is the enterprise case (a network partition) and avoids the dev server's durability problem |
| B2 | Crash cost is dominated by detection (30 s heartbeat timeout, plus restart and backoff) | Record heartbeat timeout as an operational setting in `docs/07`, and graceful worker shutdown (finish heartbeats, release slots) as an implementation requirement in `docs/01` |
| B3 | `restart.ps1` stops **every** `dotnet` process on the machine and hard-codes `d:\Dev\...` | Track and stop only the spike's own PIDs; use a path relative to the script |
| B4 | Results are in two places: `spikes/01-interpreter/results/` and `spikes/01-interpreter/src/results/` | One place |
| B5 | Carried from review 11 | (a) When a child ends with `replan_parent`, does its branch count as completed for the join, and is the ticket re-run after the parent patch? (b) What does an expansion of zero items do? (c) Reachability wording: patch targets do not exist at template load; say the rule applies at patch time. (d) `docs/12` subsections are ordered 2.3, 2.2, 2.1 |

## Answers to section 11

| # | Question | Answer |
|---|---|---|
| 1 | Platform status versus run status, untested for want of an API | Defer to the API work. Record it in `docs/08` as an acceptance criterion the API must meet, so it is not lost |
| 2 | Byte-based threshold, or compact state | **Both, in that order.** Compact first (A1), so carried state is bounded; then events or bytes as the safety net (A2) |
| 3 | `lower` and `upper` are not in Cel.NET | Register them in the engine, using culture-invariant conversion (`ToLowerInvariant`). The culture-sensitive form is non-deterministic across machines, which is a replay hazard |
| 4 | Cost estimation is load-bearing | Yes, change the wording in `docs/09` 8.1. Also record finding 2 there: the engine must build the environment with `Env.NewCustomEnv` and explicit opt-in, or the restricted checker is not restricted |
| 5 | Pool sizing note in `docs/07` | Yes, once A3 has a real measurement behind it |
