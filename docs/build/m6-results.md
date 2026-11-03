# M6 results: findings, validators and levels

## Waits across the hand-over and child runs

Builder W1, branch `m6-shell`. Settles LOG 139 (a wait held up a run's hand-over, M6's must-fix from M5) and M3
follow-ups 2 (a child's parameters), 3 (`consumes` across runs) and 10 (a child that ends without signalling). Judgement
calls are LOG 152 to 155, unreviewed.

### What was built

| Piece | What it does |
|---|---|
| `StepInterrupted` (core) | Returns a running step to pending at the same execution: no new execution, no attempt used (F4, the attempt ceilings, unchanged). The next run starts that execution again with the same identity, so what it recorded in the record store (when it began waiting, the head it waits on) still applies (LOG 152) |
| Interrupting waits (shell) | When a run starts draining for a hand-over, it cancels the activities of `ci.checks`, `wait.condition` and `scm.merge` (`EngineActions.Waits`) and hands them back to the core once the cancellation completes. Other activities drain as before. Marker `m6-interrupt-waits`; `docs/02` section 3 |
| A child's parameters | The parent's resolved parameters for the names the child's template declares, under the `run.child` step's `with.parameters` (`RunCore.ChildParameters`; `docs/03` section 6.2) |
| `consumes` across runs | `from: parent` and `from: root` come in the child's start input, pinned when it starts; `from: { child: <id> }` reads the child's artifacts, which the parent takes from the record store's artifact index when it confirms the child's end. Check 32 covers every source. In a sandbox, an artifact from another run is named with its source in front (`parent/contract`) (LOG 153). Marker `m6-child-inputs` |
| The artifact index | The workflow writes each artifact an agent step or action reports, with its accepted outcome; the SQLite record store keeps it, once per run, step, execution and name, and reads a run with its descendants (LOG 155) |
| Silent child ends | The run that started a child awaits Temporal's child handle; a run resumed after a hand-over watches the children it carried with one activity (`WatchChildrenAsync`), which the next hand-over interrupts. Either only adds the child to the ends to confirm, as its signal does (LOG 154). Marker `m6-child-watch` |

### Checks and controls

Each check was seen to fail with the control applied, and pass again once it was restored.

| Check | Control (what was broken) | Result under the control |
|---|---|---|
| `InterruptionTests` (core): an interrupted wait with an allowance of 1 restarts at execution 1 after a carried snapshot, with no attempt counted | `Enter` ignores the resume flag | The step is exhausted; no `StartStep` |
| `WaitHandOverTests` (Temporal): a `ci.checks` on a check left in progress, beside 60 short actions; the run hands over with the wait open, which passes in a later run, started once, with its one `since` record from the first run | The shell drains waits again | "no hand-over within 90 s while the wait was open (draining True, running checks)" |
| `ChildRunTests`: the child gets `ticket_id` and `colour` from its parent and `size` from its step, and not `unrelated`; it consumes the parent's notes; the parent consumes the child's summary; the index holds both | Each separately: `ChildParameters` bypassed; the parent's artifacts not passed; the index not written; the index not read in the confirm | The run does not end (the child fails at run start, or a step stops at check 32) |
| Check 32 for `from: parent`: the child template started on its own stops at `use`, naming `parent/notes` | The old check (own run only) | No intervention |
| A child terminated before its parent hands over fails the parent's `run.child` step | The child handle not awaited | The parent never ends |
| A child terminated after its parent handed over fails the parent's `run.child` step | No watch of carried children | The parent never ends |
| SQLite artifact index (`LocalProviderTests`): once per key across stores on one file; a run's view includes its descendants only | `INSERT OR REPLACE`; no descendant tree | Order differs; the child's artifact is missing |
| Replay: five new histories (`wait_hand_over`, `wait_hand_over_resumed`, `family_parent`, `family_child`, `silent_parent_resumed`) hold the three markers, and the resumed one starts with a step to resume | Not cancelling an interrupted wait; not watching carried children | `wait_hand_over` and `silent_parent_resumed` fail to replay |

Unchanged: the SP1 scenario (`ContinueAsNewTests`), `EpicRunTests` and the seven earlier recorded histories pass.

### Tests run

Before review: default 755 passed; integration 55 passed (second full run; the first failed `WaitHandOverTests`, a test
bug since fixed, and `EpicRunTests`, follow-up 1); replay 15 passed. After the review fixes: default 756 passed,
integration 57 passed, on a clean rebuild.

### Review of PR 10 (changes required) and fixes

| # | Finding | Fix | Test, and the control that made it fail |
|---|---|---|---|
| F1 (high) | A cancel landing while a wait's read ran ended the attempt as a failure Temporal does not retry (retry state `CancelRequested`), which counted as a failed attempt; the next run started a new execution, restarting `since`, `timeout` and `grace` | Such a failure of an interrupted step counts as acknowledging the cancel, as a cancellation does (LOG 152 amended) | `WaitHandOverTests`' second case: the in-memory forge holds the wait's read (`HoldCheckReads`) until the drain has asked to cancel it. The new catch disabled: `attempt_failed` for the wait |
| F2 (low) | The exclusion of a `run.child` step's artifacts (its child's) from this run's was untested | The family test's child also writes `notes`; a second child must still get the parent's own `notes`, and nothing else. A redundant name filter in `RunArtifacts` removed | All exclusions removed, or `LatestArtifact`'s alone: the second child gets the first child's notes. The engine actions' view (`ScopeOf`) keeps its exclusion as a defence only: evidence is matched by producing action |
| F3 (low) | A refused child end became an intervention without a patch marker | Behind `m6-child-inputs` | A `run.child` step promising an artifact its child never writes opens an intervention. Without it: no wait opens |
| F4 (low) | Staged names of artifacts from other runs undocumented; a run's own artifact named `parent` would collide | Documented in `docs/04` section 3.1; check 11 refuses a produced artifact named `parent`, `root` or `child` (`docs/09`) | `MutationTests`' `source_named_artifact`. The check disabled: the case fails |

### Deviations and judgement calls

- LOG 152: one general core input, used by the shell only for the three actions that wait.
- LOG 153: a grandchild's `root` artifacts are pinned when its parent started, not when it started; artifacts from
  another run are named with their source in front in the sandbox.
- LOG 154: a silent end is noticed by the child handle, then by one watch activity per resumed run, not by a timer.
- LOG 155: the artifact index serves `from: { child: <id> }`; the parent direction stays in the child's start input.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | `EpicRunTests` failed once under the full suite's load: all three hand-overs came right after a burst of child ends, so none had a child running, and its check that one did failed. Not reproduced in two more full runs and two targeted runs. Its six children are identical ticket runs, and the fake script cannot give them different durations, so spreading them is more than a small change. A fake script keyed by a child's parameter would allow it |
| 2 | `FindingsLedgerContract` mixes findings and artifact tests, so SQLite cannot subclass it until W2 adds the ledger tables; `LocalProviderTests` covers the SQLite artifact index meanwhile. W2's SQLite subclass will cover both |
| 3 | A watch activity holds a worker slot per resumed run with carried children, for as long as they run |
