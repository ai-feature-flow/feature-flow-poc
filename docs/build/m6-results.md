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

## Findings

Branch `m6-findings` (worker W2), from `m6@c238b4d` (the shared interfaces of PR 9).

### What was built

| Part | Where | What |
|---|---|---|
| The ledger in SQLite | `Providers.Local/Stores.cs` (its own region) | Tables `findings` and `finding_events`; query `findings` over the run tree by a recursive query (LOG 160) |
| `findings.record` | `Engine/Actions/Findings.cs` | From a reviewer's `findings_report` or a findings source by instance name. Fingerprint matching against the run's and its children's ledger, re-raise or new, code change at file granularity, `reopen_on`, outputs `new`, `reraised`, `reopened`, `repeat_fixes`; `unavailable` from a source, or after 30 minutes pending (LOG 162, 163) |
| `findings.triage` | the same | States with actor and reason; fixes counted; declines posted on the open pull request, once per finding. A triage naming an unknown finding is the new outcome `invalid`, nothing applied (LOG 161, 164) |
| Older libraries still load | `Model/Actions.cs`, `Library/PlanLoader.cs` | An action outcome added later escalates when a plan does not route it (`ActionContract.Escalates`, LOG 161) |
| std | `library/std/fragments/fix_loop.yaml` | `invalid` back to the triage's producer: `apply_triage` to `triage`, `record_fixes` to `verify_fix` |
| The `findings` namespace | `Providers.Abstractions/FindingsLedger.cs`, `StepOutcome.Findings`, `RunWorkflow.Apply` | Counts returned with a findings action's outcome and applied (`FindingsChanged`) before it, so its routes read them; carried over a hand-over by the core (LOG 166) |
| The ledger view | `RunWorkflow` (check 32, agent spec), `Engine.Agent/AgentActivities.cs` | `findings_ledger` staged as a `findings_report` with ledger ids and states when the step's sandbox is created (LOG 167) |
| Copilot review source | `Providers.GitHub/CopilotReviewSource.cs`, kind `copilot_review` | Requests Copilot as a reviewer, polls, reads review comments as findings (LOG 165) |
| Stand-in replaced | `EngineActions.Built` | With real source control configured both actions run for real (LOG 121, 146) |

### Checks

| What | Test | Result |
|---|---|---|
| SQLite ledger | `Providers.Local.Tests`: `SqliteFindingsLedgerContract` (5), `SqliteLedgerTests` (1) | Pass |
| Both actions on the in-memory world | `Engine.Tests/FindingsActionTests` (30 cases) | Pass |
| Merge bar reads the counts, also across a hand-over | `Interpreter.Tests/FindingsNamespaceTests` (3) | Pass |
| A pre-M6 `fix_loop` loads, `invalid` escalating; the 4 replay histories replay | `StdLibraryTests`, `Replay.Tests` | Pass |
| std routes `invalid` back to its producer | re-drive `library/std/tests/ticket_triage_invalid.yaml` | Pass |
| std's review and fix loop on the real ledger: a blocking finding, triage and verification from the staged view, the second review clean, the ticket merges | `Integration.Tests/FindingsLoopTests` | Pass |
| Copilot source | `CopilotReviewSourceContract` (4) and `CopilotReviewSourceTests` (8), recorded GitHub | Pass |
| Copilot source, live on the fixture | `CopilotReviewSourceLiveContract` | **Unavailable**: Copilot code review is not enabled on `ai-feature-flow/feature-flow-example`; GitHub answers 201 to the review request without adding Copilot. 3 passed, 2 skipped (they need a review). Every pull request opened was closed; no `ffc/` branch left |

Counts on `m6-findings` before the PR: default suite **799 passed, 0 failed** (746 at `c238b4d`); integration **51 passed**
(Temporal on port 8233); Release build 0 warnings.

### Controls

`tools/check_m6_findings_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker, no model, no GitHub). **All 31 hold.**
Each breakage made its named tests fail and all passed after the revert. Two first failed to hold and were fixed:
"a retry applies the triage again" passed by construction (a plain retry finds the state already set, so the test now
reopens the finding between the attempt and its retry), and the Copilot `blocking` control did not build (an unused
parameter is a warning, so an error).

| Breakage | Tests that failed |
|---|---|
| Ledger: view without descendants; finding not replaced; event key appended again | the contract's tree, last-write and history tests; the child re-raise test |
| Record: no fingerprint matching; `reopen_on` ignored; file change not seen; any commit counts as a change; retry re-applied; bad report accepted; wrong `for_each` branch's report; pending source waited on for ever | the matching re-raise and child tests; the reopen_on theory; the code-change and repeat-fix tests; the other-file test; the retry test; 3 bad-report cases; the branch test; the timeout test |
| Namespace: `repeat_fixes` at three fixes; `open_blocking` counts every open finding; counts not applied by the workflow; dropped from the activity's outcome; lost at the hand-over | repeat-fix; namespace counts; `FindingsLoopTests` (twice); `FindingsNamespaceTests` |
| Ledger view: check 32 refuses `findings_ledger`; view not staged | `FindingsLoopTests` |
| Triage: unknown finding accepted; retry re-applied after the finding moved on; fix not counted; declines never posted; posted despite `post_declines: false`; unstable comment key | the invalid theory; the retry test; triage and repeat-fix tests; the decline test (three times) |
| `invalid`: no engine default; std routes it to `fix` | the old-library test and 3 replay histories; the re-drive test |
| Copilot: older head counted ready; asked again; 201 without Copilot treated as pending; whole hunk as snippet; `blocking` ignored | the matching provider tests |

### Deviations and judgement calls

- LOG 160: the SQLite contract test sends the artifact index to the in-memory store until W1's SQLite index is merged.
- LOG 161: **`findings.triage` gains `invalid`**, and an action outcome added later escalates where unrouted. docs/01
  section 5.4's mechanism (validate produced artifacts at report time, with correction turns) is not built; this is
  the routed alternative the brief asked for. Changes docs/03 section 4.3 and docs/04 section 2.
- LOG 162 to 167: matching and file-granularity code change; the source timeout and interventions for unreadable
  reports; declines posted only with an open pull request; the Copilot mapping; the namespace's definitions and
  transport (`StepOutcome.Findings`, a new optional field in `Steps.cs`); the ledger view's staging.
- Re-drive tests keep scripted `findings.*` values: `Testing.Workflows` may reference only the core (LOG 66, 80).
  The real counting runs in the engine and integration tests.
- Files outside my own that I touched with small additions: `RunWorkflow.cs` (3 places), `EngineActions.cs` (the
  `Built` set, the dispatch, `ActionRun.Instance`, `ActionOptions.ReviewTimeout`), `ActionScope.cs`, `Steps.cs`,
  `StepStartChecks.cs`, `Model/Actions.cs`, `PlanLoader.cs`, `ActionWorld.cs`, `ForgeTicketTests.cs` (std's
  reviewers now write empty reports; `StartAsync` is internal).

### Review of PR 11 (changes-required, 2026-11-03)

The independent review found 6 issues and 8 mutations its tests did not catch. Coordinator: LOG 161 and 164 accepted
for the product owner's list; the rest fixed as follows.

| # | Finding | Fix | Test (control) |
|---|---|---|---|
| F1 | A triager setting `fixed`, `escalated` or `declined` on a demonstrated blocking bug, with no fix, merged | `findings.triage` `states` (std: triager `[open, declined, escalated]`, verifier `[fixed, open]`); engine-counted outputs `escalated`, `declined_blocking`; `fix_loop` routes on them and `findings.open`, not the triager's outputs; an escalation or a declined blocking or demonstrated finding goes to a person (LOG 168) | `FindingsLoopTests.The_triager_alone_cannot_clear_...` (3 cases stop at an intervention, no deliver step; with std's `states` removed the `fixed` case merges), re-drive `ticket_triage_to_person`, engine `states` and count tests |
| F2 | A later reviewer could downgrade an open finding | A re-raise merges: blocking OR, demonstrated wins, higher severity, proof kept | `A_weaker_re_raise_does_not_downgrade_the_finding` |
| F3 | Parallel recorders could apply stale counts | A ledger version (event count, read with the counts) travels with them; the core keeps the newest, across a hand-over. The finding row's lost update is logged, not fixed (LOG 169) | `Counts_read_at_an_older_ledger_version_...`, `Each_findings_action_returns_a_higher_ledger_version` |
| F4 | Two problems of one rule and file without symbol or snippet merged silently | The title joins the fingerprint then; a duplicate in a report is noted | `Two_problems_of_one_rule_in_one_file_...` |
| F5 | LOG 161's default could widen unnoticed | Test that only `findings.triage invalid` escalates by default and an unrouted `analysis.run failed` is still check 7 | `Only_an_outcome_added_later_escalates_...` |
| F6 | Mutations A, C, D, F, H, N, O uncaught | Tests for each, H being the state revision moving at the fix | one test each; controls A to O |

Re-check (approve-with-fixes): R1, a no-op fix with a verifier writing `fixed` merged; now a move to `fixed` is
`invalid` while nothing is committed to the finding's repository since it was raised or reopened
(`The_verifier_alone_cannot_clear_...` and two engine tests; removing the rule merges). R2, `record_fixes`'s `states`
guard is tested (a verifier's decline stops at `verify_fix`) and it routes on the engine's counts (re-drive
`ticket_verifier_to_person`). R3, `states` fails closed: a given list allows exactly its states; check 31 requires a
non-empty subset of the four. All in LOG 168 (my range was used up).

Controls are now 53 (18 added for the review and 4 for the re-check; existing ones retargeted at changed text). Counts after the fixes:
default **815 passed**, integration **54 passed**, Release build 0 warnings.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Validate produced artifacts at report time against their types (docs/01 section 5.4), so a bad `findings_report` or triage goes back to the agent in its session rather than to an intervention or a re-run |
| 2 | std's `ticket_delivery` posts no declines: its fix loop runs before the pull request opens (LOG 164). Post the ledger's declines when `scm.pull_request` opens one |
| 3 | A parent's `findings` counts change only at its own findings actions; a child's change shows at the parent's next one |
| 4 | Copilot review could not be shown reviewing live: enabling it on the fixture is the product owner's choice |
| 5 | M5 follow-up 3 (a re-run reviewer is not told which files it left unread) was not picked up |
| 6 | A fixed rule vocabulary in std's reviewer task: free-text rule ids from context-free reviewers give a new finding per wording, so `repeat_fixes` rarely accrues (review design note) |
| 7 | A person cannot change a finding's state until M7 wires decisions' sessions to `findings.triage`; an escalation is answered by the intervention's recovery actions meanwhile (LOG 168) |
