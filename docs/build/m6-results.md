# M6 results: findings, validators and levels

## Summary

M6 is done. The engine now runs epics and projects, not only single tickets.

- **Done-when 1:** std `epic_delivery` with three tickets, each a GitHub issue, ran on `feature-flow-example`. The
  engine merged it into its parent branch (`run-93ee2dd4bf56`, merge `8e7a697`).
- **Done-when 2:** std `project_delivery` delivered to `feature-flow-example`, then `feature-flow-example-client`. The
  product owner's merges into `main` were detected and `delivery.report` said `complete` (`run-cc7255f7fa09`).
- **Verified-by:**
  - each standard validator has a failing and a passing fixture;
  - an agent step declaring an evidence type is refused at load (check 12);
  - `tests_added` catches a fix that adds no test.
- **Must-fix from M5 (LOG 139):** a wait no longer holds up a run's hand-over to a fresh Temporal run (LOG 152).

How it ran: shared interfaces first (PR 9), then four builders in parallel (PRs 10 to 13), then integration and the
live runs (PR 14). Each pull request had an independent review and a re-check.

- Every first review was "changes-required", and every catch was real. The most serious:
  - a triager agent alone could close a demonstrated bug and merge (LOG 168);
  - a planner could mark its own criteria as "owned" and merge with no tests (LOG 178);
  - `delivery.report` counted a stale or reverted merge (LOG 180);
  - a wait cancelled mid-read used an attempt (LOG 152).
- The live runs found 14 more seams (LOG 190 to 200).

Tests on `m6` at the end: 965 default, 78 integration.

The sections below are each worker's own, in build order, then integration. They are followed by the coordinator's
decisions, the list for the product owner, and the consolidated follow-ups.

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

---

## Validators and evidence

Worker W3, branch `m6-validators`, from `m6@c238b4d` (PR #9's shared interfaces). Judgement calls LOG 170 to 177,
unreviewed.

### What was built

| Part | Where | What |
|---|---|---|
| Standard validator provider | `src/Providers/FeatureFlow.Providers.StdValidators/` | Kind `std`, the seven validators of `docs/04` section 10.2 (rules where the design is open: LOG 170). Reads artifacts from the store, test files through the code host (`tests_added`, LOG 172), ancestry through the VCS provider's clones (the trace). Registered in the three hosts |
| `artifact.validate` | `src/FeatureFlow.Engine/Actions/Validation.cs`, one line each in `EngineActions` | Subjects from what the step consumes, freezing at work branch heads, the run tree's index and ledger, every validator provider searched, the `validation_report` and offered reports kept as the step's artifacts; interventions where it cannot check (LOG 173). Replaces M5's stand-in when real source control is configured (LOG 121, 177) |
| Engine-only evidence (F7) | `Check.EngineEvidence`, `Check.OwnRuns` | Evidence is an index entry an engine test action made, of the run being checked; an agent's artifact of the same name or type is never evidence. Check 12 at load is M1's, with its std mutation |
| Passing test names | `TestResults.Passing`, `ResultFiles` | So a claimed test can be checked against what the engine saw pass (LOG 171, changes a frozen record) |
| Expression values | `CelExpressions` | Maps in an expression's value come back as plain maps (LOG 174); std's `level_validators` reached the action as pairs |
| Std | fragments, templates, `fix_findings` | Validate steps keep their report on both outcomes (LOG 175); the fixer gives each regression test's `source.file` |

### Checks

| What | Test | Result |
|---|---|---|
| **Each validator has a passing and a failing fixture** (M6 verified-by) | `StdValidatorsContract` (the PR #9 `ValidatorContract`): 18 fixtures, each failing one for its named code | Pass |
| **A fix that adds no test is caught by `tests_added`** (M6 verified-by) | `StdValidatorTests.Tests_added_catches_a_fix_that_adds_no_test_in_a_real_git_history`: a real git repository; finding at A; a code-only fix at B is invalid; the test added at C is valid; a changed file lacking the claimed test is invalid | Pass |
| **An agent step declaring an evidence type is refused at load** (M6 verified-by, check 12) | M1's `MutationTests` `agent_produces_evidence` | Pass |
| F7 at run time | `Test_results_not_made_by_an_engine_test_action_prove_nothing_to_the_trace` (3), `Only_test_results_an_engine_test_action_produced_satisfy_a_claim` (3), the contract's agent fixture | Pass |
| A child's evidence is not the parent's | `Satisfaction_counts_only_this_runs_test_results_not_a_childs` | Pass |
| The trace through an owner into a child and a squash merge; its gaps | `StdValidatorTests` (3) | Pass |
| `artifact.validate` | `ValidationActionTests` (14): std's ticket plan check, invalid with report kept, missing and unreadable subjects, refusals, index and ledger and cross-run subjects, trace report kept, F7, an unresolved parent subject in a child run | Pass |
| A std ticket validated for real | `ForgeTicketTests` (6): `plan.validate` and `verify.satisfaction` run the std validators | Pass with SQLite answering `artifacts` and `findings` (see Deviations) |

Default suite: 778 passed, 0 failed before the last commit; provider and action tests after it: 18 and 14 passed.

### Controls

`tools/check_m6_validators_controls.py`: 29 controls, no Temporal needed. Each breaks one rule, its tests fail, and
all pass after the revert. **All 29 hold.**

| Breakage | Tests that failed |
|---|---|
| Each validator's rule (link resolution, coverage, missing owner, dropped inherited item, rewording, absent parent plan, no method) | The contract's fixtures; for the last two also `ValidationActionTests` |
| Satisfaction passes any claimed test; counts an earlier revision; counts a child's results | The contract; `Satisfaction_checks_the_frozen_head_...`; `Satisfaction_counts_only_this_runs_...` |
| F7: any producer's test results count | The contract, the trace theory (3), the action theory (2) |
| Trace: merges ignored; every revision contained; owners not followed | The three trace tests |
| `tests_added`: unchanged file counts; file lacking the test counts; inapplicable regression test still required | The real-git test, the contract, the applicable test |
| No passing names (TRX, JUnit); maps not converted | `ResultFileTests` (3); the expressions test |
| `artifact.validate`: optional absent stops; required missing absent; unresolved parent subject absent; branch name frozen; descendants dropped; offered report not kept; default provider only; not dispatched | The matching `ValidationActionTests` |
| Check 12 disabled | `Mutation_triggers_its_check_and_no_other` |

### Deviations and judgement calls

- LOG 170 to 177. For a look: **171** (a frozen record changed), **175** (std changed), **176** (schema and
  structure checks from artifact types deferred to the agent step).
- `ForgeTicketTests` need the SQLite record store to answer `artifacts` (W1, committed on `m6-shell`) and `findings`
  (W2). Verified here with a temporary, uncommitted stub returning no rows; without them the forge ticket stops at
  `plan.validate`.
- `ActionScope.Consumed` must be filled by the workflow (`RunWorkflow.ScopeOf`, W1's file): until then a child run's
  subject from its parent stops the step instead of passing as absent.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Integration: `ScopeOf` passes `Consumed`, keyed by `ConsumedKey`, from `ConsumedArtifact` |
| 2 | Schema and structure checks of produced artifacts at the agent step (LOG 176) |
| 3 | `verification_satisfaction` does not check that a review accepted the claims revision (`semantic_review: required`) |
| 4 | The trace does not check that a child completed, only that its plan, claims and passing tests exist and landed |
| 5 | M5's live-run how-to needs `validator: { instances: { std: {} } }` |

### Review of PR 13 and its fixes

The independent review found changes required: two high, two medium and three low. Governing principle (the
coordinator's): a run's acceptance criteria are met only by engine test evidence at the frozen head, or by a person,
never by the agent's own classification. LOG 178 and 179.

| Finding | Fix | Test | Control |
|---|---|---|---|
| F1 (high): a planner marks its criteria as owned and a ticket merges with no test | An owner counts only when it is a child kind the plan's scope may name and not the run itself; otherwise the item needs a method, and satisfaction reports it `unproved`; satisfaction excuses a delegated criterion only when its owner's plan holds it; inheritance refuses owners in a child's plan with or without a parent | `ForgeTicketTests.A_planner_that_marks_its_criteria_as_owned_to_skip_testing_does_not_get_a_merge` (the reviewer's probe: no merge); fixtures for `owner_in_child`, `no_method`, `unproved` (self-owned, and owner never planned) and a delegated pass | Old owner rule restored: the probe completes and merges (run by hand with the SQLite stub; in the script) |
| F2 (high): `tests_added` passes a claim naming the changed production file, or any symbol | A claimed test must have passed in this run's results at the frozen head, and its file must not be the finding's, must hold the test's name (the claim's `symbol` ignored) and must have changed; std's `fix_loop` re-tests before `tests_added` | Fixtures: test not passed, production file, free-text symbol, other repository; the real-git test now supplies the engine's results at each head | Each rule removed fails its fixture |
| F3 (medium): a subject resolved to the last step id's producer | The agent step's rule: own scope first, then the first by step id | `A_subject_with_several_producers_is_the_one_an_agent_step_beside_it_would_consume` | Reversed order fails it |
| F4 (medium): many error codes and the trace's run restriction unpinned | One failing fixture per code (41 fixtures); trace tests for tests and claims only from the holder's run; ancestry without a VCS provider | `StdValidatorsContract`, `The_trace_takes_tests_and_claims_only_from_the_run_whose_plan_holds_the_item` (2), `Tests_at_an_ancestor_of_the_head_...` | 26 new controls, one per surviving mutation the review listed |
| F5 (low): sets untested | — | `A_set_made_by_artifact_compose_is_passed_with_its_members` | Sets never expanded fails it |
| F6 (low): infrastructure errors untested | — | `A_validators_infrastructure_error_is_retried_not_an_intervention` | Made an intervention fails it |
| F7 (low): an empty plan passes | An empty plan is `malformed` | Fixture | Made a warning fails it |

Std changes for the fixes: `fix_loop` gains `retest`; the readable-form goldens and the plan-size mutation's step
count follow it. Not changed: a subject's `own_run_unknown` when byte-identical claims are shared with a child (rare,
fails closed); `SameRepository` suffix matching (low); a test that passes but does not test the finding is the
reviewer's call.

After the fixes and the merge of PR #10 (`62b6d93`): default suite 795 passed, 0 failed; integration 52 passed,
6 failed, all `ForgeTicketTests` stopping on SQLite's `findings` query (PR #11). With only that query stubbed (no
rows, not committed), all 7 `ForgeTicketTests` pass on #10's real artifact index. Controls: 57 of 58 hold; the F1
end-to-end control needs `findings` too and held when run by hand with the stub.

### Re-check of PR 13 (approve-with-fixes) and the merge of PR #11

LOG 179, extended.

| Finding | Fix | Test | Control |
|---|---|---|---|
| N1 (high): at the merge, W2's `record_fixes` routes ended at `tests_added`, so `retest` was unreachable | The last route goes to `retest`; goldens regenerated | `ticket_fix_loop` requires `verify.fix.retest`; new `ticket_fix_retest_failed` (a failed re-test sends the fixer back, then `fixed`); `FindingsLoopTests` path includes `retest` | Route back to `tests_added`: both std scenarios fail |
| N2 (medium): the re-test's `test_results` shadowed `verify.tests`' for `repair` | Named `fix_test_results`; `fix` consumes it, optional | Goldens, std scenarios | — |
| N3 (low): a whitespace edit or another changed file with an old test's name passed | A test claimed `new` that passed where the finding was raised does not count; paths normalised | Two fixtures | Each rule off fails its fixture |
| N4 (low): four guards unpinned | `Delegated`'s redundant id guard removed | Fixtures: another epic as owner; owner's plan in this run; owner's plan without the item; test passed only at an earlier revision; subjects from two runs | One control each |
| N5 (nit): misleading message | "no test results of this run at that revision" | — | — |

`FindingsLoopTests` (W2) now scripts the fixer's regression test (a claim of `Regression.fix` in the file the fixer
wrote, which the forge's TRX reports passing), since `tests_added` needs real evidence.

After the merge of PR #11 (`5940c1f`) and the re-check fixes: default suite 869 passed, 0 failed; integration 64
passed, 0 failed, including all `ForgeTicketTests` and `FindingsLoopTests` on SQLite's real index and ledger (no
stub). Controls run for this round (the review's and re-check's, 22): all hold, the F1 end-to-end control included.

## Delivery, artifacts and tracker

Branch `m6-delivery`, from `m6@c238b4d` (the shared interfaces, PR 9). Worker W4. Judgement calls: LOG 180 to 188,
unreviewed.

### What was built

| Part | Where | What |
|---|---|---|
| `delivery.report` | `Engine/Actions/Delivery.cs` | Per repository: the run's `merge_record` made by `scm.merge` into the parent branch, counted only if the branch still contains it. `complete` only if every repository merged. Outputs `merged`, `not_merged`; a `DeliveryReport` when declared (LOG 180) |
| `artifact.compose` | `Engine/Actions/Artifacts.cs` | Named artifacts into an `ArtifactSet` at their exact references |
| `artifact.import` | same | Files from a repository at a full commit id, each the artifact it names; YAML and JSON must parse; an `ImportRecord` (LOG 181). The session form is M7's |
| `artifact.publish` | same | One commit through an engine sandbox on `ff/publish/<run>/<step>/<execution>`, a pull request into the default branch; `conflict` (nothing written) when the destination or the branch moved; supersedes an open earlier publication in place; a `Publication` record (LOG 182) |
| `tracker.update` | `Engine/Actions/Tracker.cs` | `create`, `update`, `transition`, `link`, `comment`; the issue defaults to the run's; `not_found` (LOG 183) |
| GitHub Issues tracker | `Providers.GitHub/GitHubIssuesTracker.cs` | Kind `github_issues`: statuses as labels or `closed`, parents as sub-issues, links as keyed comments, no custom fields; reads past GitHub's list lag (LOG 184) |
| The run's ticket | API, CLI, `Wire.cs`, `Engine.Agent/AgentActivities.cs` | `tracker_issue` on the run request and on `run.child`; each agent step stages the issue as `/ff/inputs/ticket.md`; std's tasks read it (LOG 185). Replaces M5's ticket file (LOG 146) |
| Declarative tracker updates | `Engine/TrackerUpdates.cs`, `RunWorkflow.cs` | From the timeline before each record: steps' and fragment uses' `on_start` and `on` (LOG 186). Patch `m6-tracker-updates` |
| Work-item budget (F11) | `Engine/WorkItems.cs`, `DeliveryActivities.cs`, `RunWorkflow.cs`, `Configuration.cs`, `RunStartChecks.cs` | The root's budget within `policy.budgets.max_work_items.ceiling` (check 22); counted in the record store before `run.child` and `plan.expand`, an allowed child recorded at once (LOG 187). Patch `m6-work-items` |
| `for_each` | `Library/PlanLoader.cs` | A list not known when the run starts is refused (check 5), not run as one empty branch (LOG 188). Load-time expansion over `run.repositories` was already M1's |

Changes to W1's files are kept small and marked: `RunWorkflow.cs` (the action scope and agent spec carry the
tracker issue; `RecordAsync` asks for tracker updates; `StartChildAsync` and `plan.expand` ask the budget; one
"Delivery" region), `Activities.cs` (`partial`, and `RunInfoOf` passes the tracker issue), `Wire.cs` (two trailing
`RunStart` fields, one `RunPolicy` field, `WorkItemBudget`). Shared test harness: `ActionWorld` gained a tracker,
the tracker issue and declared produces.

### Checks

| What | Test | Result |
|---|---|---|
| `delivery.report` | `Engine.Tests/DeliveryActionTests` (6) | Pass |
| Artifact actions | `Engine.Tests/ArtifactActionTests` (14) | Pass |
| `tracker.update` | `Engine.Tests/TrackerActionTests` (9) | Pass |
| Tracker updates due, budget values, `for_each` at run start | `Engine.Tests/TrackerUpdateAndBudgetTests` (4) | Pass |
| GitHub Issues tracker | `GitHubIssuesTrackerContract` (`TrackerContract` over a fake GitHub whose lists lag) and `GitHubIssuesTrackerTests`: 19 | Pass |
| GitHub Issues, live | `GitHubIssuesTrackerLiveContract` (Live) on the fixture: 9, every issue closed after | Pass, with the approved token (see below) |
| Ticket staged for the agent | `Engine.Agent.Tests/AgentManifestTests` (2 new) | Pass |
| `tracker_issue` in the API, the ceiling in configuration | `ApiTests` (2 cases), `ConfigurationTests` (4 cases) | Pass |
| Through the engine | `Integration.Tests/DeliveryRunTests` (6): budget across a tree, children started together, `plan.expand` refused whole, the organisation ceiling and check 22, tracker updates through a fragment exit, a failing update noted | Pass |
| Replay | `delivery_tracked` and `budget_middle` histories, each holding its patch marker | Pass |

**The App's issues permission.** At first the App installation had no `issues` permission, so the first Live runs used
the approved personal token (`FF_GITHUB_TRACKER_TOKEN`). The product owner has since granted it; see the review section.

**Bugs the tests found.** The first live run failed twice: GitHub's issue lists lag a create by about five seconds
(measured), so adoption by marker made a second issue and a label search found nothing. Searches now read the issue
numbers above the newest listed one directly, and match labels themselves. The integration tests found two races in
declarative updates: a step that ended while an update was awaited, or between the last update and the record, was
recorded without being examined (2 failures in 5 runs before the second fix, 0 in 6 after).

### Controls

`tools/check_m6_delivery_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker, model or GitHub). Each breakage was run, its tests failed, and all passed again after the revert. **All 45 hold** (31 first, 14 added for the review of PR 12). One did not at first: a retried `artifact.publish` re-made an identical commit, which the memory forge names by its content, so the test passed; it now also checks that the retry opens no sandbox.

| Breakage | Tests that failed |
|---|---|
| Delivery.report counts a merge record without confirming the parent branch holds it | `A_merge_the_parent_branch_no_longer_contains_is_not_delivered` |
| Delivery.report is complete when any repository merged | `Each_named_repository_is_reported_and_one_unmerged_makes_it_partial` |
| Evidence is read from any producer, an agent's merge_record included (F7) | `An_agents_artifact_named_merge_record_is_not_a_merge` |
| Artifact.import takes a branch or short id as its commit | `Import_refuses_a_commit_that_is_not_a_full_commit_id` |
| Artifact.import registers a YAML file that does not parse | `Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository` |
| A path with .. is taken as inside the repository | `Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository` |
| Artifact.publish ignores a destination changed since the earlier publication | `A_destination_changed_since_a_merged_publication_is_a_conflict_and_nothing_is_written` |
| Artifact.publish overwrites a publication branch someone else moved | `A_publication_branch_someone_else_moved_is_a_conflict` |
| Artifact.publish trusts an agent's artifact as the earlier publication | `Previous_must_be_a_publications_own_record` |
| A retried artifact.publish writes a second commit | `A_retried_publication_adopts_its_commit_and_pull_request` |
| Artifact.publish opens a new pull request instead of superseding an open one | `Republishing_over_an_open_pull_request_supersedes_it_in_place` |
| Tracker.update creates without the step's key, so a retry makes a second issue | `Create_makes_one_issue_and_a_retry_of_the_same_step_adopts_it` |
| Tracker.update stops for a person on an issue that does not exist | `An_issue_that_does_not_exist_is_not_found` |
| Tracker.update does not default to the run's tracker issue | `Transition_comment_and_update_act_on_the_runs_issue_by_default` |
| The GitHub tracker trusts GitHub's lagging lists alone | `A_create_retried_before_GitHubs_lists_show_the_first_adopts_it`, `Creating_twice_with_one_key_creates_one_issue` |
| A transition keeps the other status labels | `A_transition_leaves_one_status_label_and_done_closes_the_issue` |
| An update drops the hidden marker, so a retried create makes a second issue | `An_update_keeps_the_marker_and_the_status_label` |
| A pull request's number reads as an issue | `A_pull_requests_number_or_a_deleted_issue_is_not_found` |
| A fragment use's on.<exit> is not seen | `A_step_and_a_fragment_use_move_the_issue_on_start_and_on_an_exit`, `Std_epic_delivery_moves_its_issue_to_done_only_when_the_epic_merges`, `Declarative_updates_move_the_runs_tracker_issue_as_steps_start_and_a_fragment_leaves` |
| Tracker updates run without their patch marker, so a recorded history no longer replays | `replay of delivery_tracked` |
| The agent step is not given the run's ticket | `The_runs_ticket_is_read_from_the_tracker_and_staged_for_the_agent`, `A_ticket_the_tracker_does_not_have_stops_the_step_for_a_person` |
| The API takes any text as a tracker issue | `A_tracker_issue_that_is_not_a_key_is_refused` |
| A root's budget ignores the organisation's ceiling | `A_roots_budget_is_its_templates_within_the_ceiling_or_the_ceiling_alone` |
| The organisation's ceiling is read wrongly | `The_work_item_ceiling_is_read_from_policy_budgets` |
| Check 22 lets a template ask for more work items than the ceiling | `The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more` |
| Run.child starts a child without asking the budget | `The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count`, `The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more`, `Children_started_together_are_counted_as_each_is_allowed` |
| A child gets no budget from its root, so it uses its own | `The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count` |
| Plan.expand adds children past the budget | `A_plan_expand_past_the_budget_is_refused_before_any_child_starts` |
| An allowed child is not recorded until it records itself | `Children_started_together_are_counted_as_each_is_allowed` |
| The budget check runs without its patch marker, so a recorded history no longer replays | `replay of budget_middle` |
| A for_each over a list known only at run time starts with one representative branch | `A_for_each_over_a_list_known_only_at_run_time_does_not_start` |

Not in the script: the race in `RecordAsync` (the previous code failed the integration test 2 times in 5; a
deterministic mutation would need a scheduling hook).

### Review of PR 12 (changes-required) and fixes

| Finding | Fix | Commit |
|---|---|---|
| F1 (high): a stale merge record read as delivered after more work | A merge counts only for the work branch's current head (LOG 180) | `81c1dc5` |
| F2: a reverted merge read as delivered | `scm.revert` produces `revert_record` evidence (new std engine type); a merge whose revert is merged or open is not delivered | `81c1dc5` |
| F3: check 22 refused a child whose own budget was above the ceiling | The F11 cap applies to roots only (LOG 187) | `6b6e143` |
| F4: a tracker outage stalled the run | Five tries over about 30 s, then a timeline note (LOG 186) | `6b6e143` |
| F5 to F8 (low) | LOG 188 says why no patch marker; a failed child start stays counted (LOG 187); LOG 187 reworded on agents and siblings; comment-list lag noted (LOG 184) | `6b6e143` |
| 8 surviving mutations | Tests for 7; dropping the empty-revision filter is equivalent on the memory VCS (an empty revision reads as not found, which is skipped) | `81c1dc5`, `6b6e143` |

After merging `m6` with W1's PR 10 (`beb311b`): build 0 warnings, default suite 834 passed, integration 65 passed,
controls 45 of 45 hold (run before the merge; the merge touched no line they mutate except beside `StartChildAsync`,
whose budget path the integration tests cover).

Product owner decisions (2026-11-04): the App has "Issues: read and write". The broker's engine ceiling now includes
`issues: write`, and agents still get no issues access (`201f981`). The Live contract ran again with the App's credential:
9 passed, and every issue was made by `ai-feature-flow-agent-access[bot]`, closed by the test, then deleted. Open question
2: a `plan.expand` path ending in `?` is optional, and std's `plan_children` passes `$.tracker_issue?` (LOG 185). Open
question 3 was accepted as recommended.

### Re-check of PR 12 (approve-with-fixes) and fixes

| Finding | Fix | Commit |
|---|---|---|
| N1: the revert guard depended on the step declaring `revert_record` | Every engine action's evidence is indexed by the engine as `evidence:<type>`, declared or not; `delivery.report` reads reverts from the index. A revert of a different pull request does not count (LOG 180) | `43eadf2` |
| N2: a work branch deleted after a person's merge read as undelivered | A missing work branch is no later work: its merges count if the target contains them and they are not reverted (LOG 180) | `43eadf2` |
| N3: a hanging tracker could hold a run for about ten minutes per update | Each try at most 30 s, all tries within two minutes (LOG 186) | `43eadf2` |
| Two survivors | A revert of another pull request, and revert evidence indexed by another action, are tested | `43eadf2` |

After merging `m6` with W2's PR 11 (`88e4604`; merge `c78bfbf`): build 0 warnings, default suite 912 passed, integration
71 passed. Controls: the 11 delivery and re-check controls hold; the full script (50 controls) was stopped after 7, all
holding, because each control now takes several minutes.

### Merge with W3 (PR 13)

Merged `m6` at `b7c72ce` (`525d4c7`). `ActionScope` keeps W3's `Consumed`, then `TrackerIssue`, passed by name. Evidence
is now indexed once (`a5b2149`): the workflow indexes declared artifacts, and the engine indexes only evidence no declared
artifact carries, `artifact.validate`'s report included. So W3's validators, which match engine evidence by type in the
index, see no duplicates. After the merge: build 0 warnings, default suite 954 passed, integration 72 passed
(`ForgeTicketTests` included). The 18 controls on the merged and changed files hold.

### Deviations and judgement calls

- LOG 180 to 188, all unreviewed. The ones marked "Needs a look": 181 (no schema check on import), 184 (the App's
  missing `issues` permission), 185 (no comments in the staged ticket), 186 (guarded exits not seen; refused
  updates noted, not retried as docs/03 said), 187 (the concurrent-creation race; unlimited with no budget and no
  ceiling).
- `run.child` gained an optional `tracker_issue` input (LOG 185).

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Schema validation of imported artifacts against their types (LOG 181); refusing evidence types on `artifact.import` at load belongs with check 12 for actions |
| 2 | The staged ticket has no comments: `ITrackerProvider` reads none |
| 3 | Declarative `on.<exit>` through a guarded route; nested fragment exits |
| 4 | Two runs of one tree creating children at the same instant can each pass the last free work item (LOG 187); a child that fails to start stays counted |
| 5 | `wait.condition` on a tracker issue (M5 follow-up 8) can now use the tracker provider |
| 6 | No engine default ceiling: with no budget and no organisation ceiling a tree is unlimited (accepted; set a ceiling in shipped profiles) |

## Integration

Worker W5, branch `m6-integration`, from `m6` at `4ff0db2` (PRs 10 to 13 merged). Judgement calls LOG 190 to 199,
unreviewed. Temporal on port 9033; hosts, model `haiku` and `FF_SECRETS_FILE` as M5's live run; the App's credential
for the tracker. Controls: `tools/check_m6_integration_controls.py`.

### Seams found and fixed

The coordinator's two seams were checked first; the live runs found the rest. Each fix has a test, and each test a
control that makes it fail (the script), except where noted.

| # | Seam | Fix | Test (control) |
|---|---|---|---|
| a | std `fix_loop` leaves by `fixed` once `tests_added` is valid, without reading `findings.open` | None needed: the caller re-tests, re-reviews and applies the merge bar (`open_blocking == 0`). A non-blocking finding left open stays open in the ledger and the ticket merges; a blocking one left open goes round the fix loop again and nothing merges | `FindingsLoopTests.After_the_fix_loop_...` (2 cases; the merge bar ignoring open blocking findings fails both) |
| b | `findings.triage`'s "a fix needs a commit" skipped findings with no `RaisedRevision`, which only a finding in a repository the run did not prepare has, and a reviewer writing `api` for `acme/api` made one | `findings.record` names the repository as the run does; a move to `fixed` with no raised revision is `invalid` (LOG 190) | Two `FindingsActionTests` (each rule removed fails its test) |
| 1 | `artifact.validate` in a child could not read `parent/...` subjects (W3's follow-up 1): every live ticket's `plan.validate` stopped | The workflow fills `ActionScope.Consumed` as for an agent step (LOG 191) | `ChildRunTests.A_childs_artifact_validate_...` on the forge (no consumed artifacts fails it) |
| 2 | Planners link to `parent/requirements`, the sandbox's name; validators knew bare names only | `requirement_support` and `requirement_inheritance` read both (LOG 191) | 4 fixtures, 4 controls |
| 3 | Reviewers invented the findings report's fields and wrote YAML that does not parse | std's review skill shows both formats, prose as block scalars (LOG 192) | Library content; std tests pass |
| 4 | After a sibling merged into the epic's branch, the next ticket's reviewers took the epic's new head as their baseline, which their checkout lacked ("bad object"): all five exhausted their attempts | `IVcsProvider.MergeBaseAsync`; the agent step's baseline is where the work branch parts from its parent (LOG 193) | `ForgeTicketTests.A_sibling_merging_...` (baseline back to the parent's head fails it); contract test for Git and memory |
| 5 | An implementer spent five minutes downloading a .NET SDK into its sandbox to run a CI-only tier | std's tasks say a `targets: [ci]` tier runs only in CI (LOG 194) | Library content |
| 6 | Planners guessed `owner`'s shape; the project planner keyed its work list by `id` | std's verification skill and planner tasks show them (LOG 195) | Library content |
| 7 | A child's parent branch existed only where the parent's own steps had touched a repository: the client epic's first step stopped | `run.child` makes the parent's work branch in the child's repositories first (LOG 196, patch `m6-parent-branches`) | `ChildRunTests.A_child_in_a_repository_...` (activity skipped fails it) |
| 8 | std `plan_children` could not resolve an epic's own requirements' links to its project's | Its check includes the parent's requirements (LOG 197) | Library content; seen live |
| 9 | The project tested none of its heads, so `scm.merge` (merge only a head this run tested) refused both repositories | std `project_delivery` gains `tests` before the trace (LOG 197) | `project_main_path` requires `tests_0`, `tests_1` (routing around it fails it) |
| 10 | A planner wrote a repository without its owner; the broker refused it | `run.child` names repositories as the run does (LOG 198) | The branching test names the child's repository `client` (names kept as written fails it) |
| 11 | A ticket's check also checked its epic's requirements' links to the project's, which it is not given | `requirement_support` checks only this run's documents (LOG 199) | Fixture (parent's links checked again fails it) |
| 12 | Copied criteria had to keep exactly the parent's links, while std needs each own requirement supported: three planners flipped between two errors | A copy keeps every parent link and may add its own (LOG 191, amended; docs/04 s10.2 changed) | Fixture; 2 controls |
| 13 | The specification reviewers never saw the specification: `spec_refs` reached them as hashes | std's reviewers consume `requirements`, `delivery_plan`, `contract` (LOG 192, extended) | Library content; seen live |
| 14 | A conflict resolver could not merge its parent branch: the sandbox held only the work branch | The checkout also holds the parent branch at its head (LOG 193, extended) | **Live only** (the Git checkout runs only in the Docker agent harness; follow-up 1) |

Not seams on inspection: the findings namespace across a hand-over with a wait interrupted (W1's `WaitHandOverTests`
and W2's `FindingsNamespaceTests` cover each half; no live hand-over interrupted a wait, see below); a child's findings
in its parent's counts (they show at the parent's next findings action, W2's follow-up 3; the epics' reviews counted
their tickets' findings as prior).

### Done-when 1: `epic_delivery` with three tickets (complete)

The epic and its tickets were GitHub issues made by the App: epic #179 ("widget query helpers") naming tickets #176
(find by name), #177 (count by prefix) and #178 (sorted names), each with acceptance criteria and its own new files.
Parent branch `ff/m6-live/epic-base` from `main` (`af06a37`). Started with `--continue-as-new-events 200`.

**F1 control** (`run-f37cfe1412e7`): the same start without `--parent-branch` was refused at run start, check 23
("deliver.merge asks for an engine merge into ... default branch main"); no branch was made.

**Attempt 1** (`run-63c1441a1879`, aborted): found seams 1 to 3; each child's `plan.validate` was recovered by hand
after the fix, then every reviewer's report failed to parse. Aborted, because the library is pinned at run start.

**The run** (`run-93ee2dd4bf56`, **completed**, 22:33 to 23:27 UTC, 54 minutes, including the waits for the fixes of seams 3 and 4):

| Step | What happened |
|---|---|
| `baseline.tests` | Both tiers in CI at `af06a37`: unit 3 passed, integration 1 passed |
| `design`, `validate_design` | Tracker moved #179 to in progress; requirements and contract written; `requirement_inheritance` and `requirement_support` valid |
| `tickets.plan`, `tickets.validate` | First plan malformed (`owner` as a string, seam 6), sent back by the validators; second valid: three items with `tracker_issue` #176 to #178 |
| `tickets.approve` | Approved by a person (`stuart`) through the CLI |
| `tickets.children_0..2` | Three ticket runs, two at a time. Each: baseline, plan validated against the epic's (`parent/...`), spec auto-approved, implement, tests in CI, five reviewers on the real ledger, satisfaction on CI test names, PR into the epic branch, checks, catch-up, **engine squash merge**, tracker to done. Ticket 2 (#177) hit seam 4 (reviewers granted one attempt each after the fix) and seam 3's YAML twice (`go_to` its reviewer), then caught up with its two merged siblings and merged |
| hand-over | The epic handed over at 208 events **with ticket 2 still running** (`RunningChildren: 1`), and again before `deliver.checks`. Ticket 2 handed over twice, once **with an intervention open** (`OpenWaits: 1`). No hand-over fell during a CI wait (`InterruptedWaits: 0` throughout) |
| `verify` | Tests at the epic head `8bebe23`: unit 20 passed, integration 1 passed; five reviewers, 4 findings, none blocking |
| `trace` | `requirement_trace` valid |
| `deliver` | PR #183; checks passed; **merge commit `8e7a697`** into `ff/m6-live/epic-base`, by Feature Flow; tracker moved #179 to done |

Checked on GitHub afterwards: `8e7a697` has parents `af06a37` (the base) and `8bebe23` (the epic head) and the trailer
`Feature-Flow-Merge: run-93ee2dd4bf56/deliver.merge`; the epic branch below it is three squash commits, `76d59d3`,
`c4036c5`, `8bebe23`, each one parent and its ticket's trailer; the change against `main` is exactly the three new
source and three new test files; `main` stayed `af06a37`; PRs #180 to #182 closed with their squash commits, #183
merged; issues #176 to #179 closed by the tracker.

### Done-when 2: `project_delivery` over two repositories (complete)

The project's requirements session is M7's, and an answer cannot carry artifacts, so the run used a run-only template,
`m6live:templates/project_delivery_imported`: std's `project_delivery` with `define` an `artifact.import` of
`projects/widget_count/requirements.yaml` and `architecture.md` at a pinned commit of the scratch branch
`ff/m6-live/project-docs` (a stand-in for the session, as M5's ticket file was for the tracker). The project: `GET
/widgets/count` in the API, then `WidgetClient.CountAsync` in the client, delivered in that order.

Attempts 1 to 6 stopped, in order, on: the person's own YAML (a `: ` in plain text; the import refused it), seam 6,
seam 7, seam 8, seam 9, and a recovery of mine (`go_to` into one branch of a finished parallel left the client epic
`incomplete`, F9 holding; follow-up 3). Each was aborted and its references deleted before the next.

**The run** (`run-cc7255f7fa09`, started 01:05 UTC):

| Step | What happened |
|---|---|
| `define`, `check_requirements`, `approve` | Imported at `0d38a4b`; valid; approved by a person |
| `epics.plan` .. `epics.approve` | Two epics, `widget_count_api` and `widget_count_client`, one per repository; valid at the first attempt; approved |
| `epics.children_1` | **Made `ff/project/widget_count` in the client repository at `b7699f6`**, for the child (seam 7), then started it |
| API epic | Two tickets (store count, endpoint); the second's catch-up conflicted with the first in `WidgetStoreTests.cs`; the resolver now merged the epic branch and resolved it (seam 14, after one retry); epic tests 6 passed; trace valid; **merged into `ff/project/widget_count` as `d2ca7cb`** |
| client epic | One ticket; its planner needed 3 + 3 granted attempts to cover its own requirements (copied the epic's instead); tests 12 passed; **merged into the client's `ff/project/widget_count` as `a562d3b`** |
| `tests` | Each project head in CI: API unit 6 passed and integration 1 passed at `d2ca7cb`; client unit 12 passed at `a562d3b` |
| `trace` | `requirement_trace` valid across project, epics and tickets |
| `deliver.deliver_0` | PR **#193** from `ff/project/widget_count` into `main`; checks passed; catch-up unchanged; `scm.merge` with `engine_can_merge: false` recorded the action request "a person merges ai-feature-flow/feature-flow-example#193 into main, or closes it to decline" and re-checks about every 35 s. The client's PR opens only after this merges (a chain) |

| person's merge | The product owner merged #193 (`d033998`, by `sparkkz-makes`). The engine's next check found it: "merged d2ca7cb852 into main as d03399841e (merge, by sparkkz-makes, already merged)"; `deliver_0.merge` completed `merged` |
| `deliver.deliver_1` | PR **ai-feature-flow/feature-flow-example-client#6** into `main`; checks (unit) passed; catch-up unchanged; `scm.merge` recorded the action request "a person merges ai-feature-flow/feature-flow-example-client#6 into main" |

| person's merge | The product owner merged #6 (`ef5b410`, by `sparkkz-makes`); the engine found it: "merged a562d3bbc0 into main as ef5b410040 (merge, by sparkkz-makes, already merged)" |
| `deliver.report` | "feature-flow-example: merged into main as d03399841e by sparkkz-makes"; "feature-flow-example-client: merged into main as ef5b410040 by sparkkz-makes"; **`complete`**; the run **completed** |

The project handed over once (at 227 events, after its children had ended); its epics and tickets handed over seven
times between them, none with a wait in flight (`InterruptedWaits: 0`).

Checked on GitHub afterwards: #193 and #6 are merged, `mergedBy` `sparkkz-makes` (not a bot); `main` is `d033998` in the
API (a squash of the project branch, one parent `af06a37`) and `ef5b410` in the client (a merge commit, parents `b7699f6`
and the project head `a562d3b`). The engine merged nothing into either `main` (F1): every engine merge was into an epic
or project branch.

Coordinator, 2026-11-04: LOG 191 (amended) and LOG 193's `MergeBaseAsync` accepted, for the product owner's review. Product owner, 2026-11-04: the project's changes stay on both
fixtures' `main`.

### Review of PR 14 (approve-with-fixes) and fixes

| Finding | Fix | Test | Control |
|---|---|---|---|
| F1 (medium, fail-open): a subject from another run the step does not consume passed as absent, so an inheritance check could pass with no parent plan | `artifact.validate` stops for a person ("not consumed by the step"); absent only for a declared optional consume (`ActionScope.Declared`, LOG 200). No load-time check: std's subjects arrive as a parameter | `ValidationActionTests.A_subject_from_another_run_the_step_does_not_consume_...` | The stop removed fails it; the reviewer's mutation (std's plan check not consuming the parent's `child_requirements`) now fails `ForgeTicketTests` |
| F2 (medium): an epic criterion linking `parent/requirements` could never be inherited | Links compared by the document each names, resolved in the run that wrote it; `requirement_ownership` refuses upward links on an item given to a child (`owned_item_links_up`) (LOG 200) | The reviewer's four fixtures (word for word and `root/` kept; bare and `parent/parent/` refused) and an ownership fixture | Parent links compared as written; word-for-word copies not counted; upward links allowed: each fails the fixtures |
| F3 (low): the parent branch in the agent's checkout was untested | `AgentStepTests.The_checkout_holds_the_parent_branch_at_its_head` (Docker harness); the side effect (an agent can merge its parent to make a commit) accepted in LOG 193 | The new test | Parent checkout skipped fails it |
| F4 (low): ambiguous names untested; two copies of the rule | One helper, `RepositoryNames.As` | Theory of 4 cases, one ambiguous | `Count >= 1` fails the ambiguous case |
| F5 (low): the current-subject fallback untested | — | `ValidationActionTests.A_subject_of_this_run_the_step_does_not_consume_is_its_latest_of_the_name` | Fallback removed fails it |
| F6 (low): no re-read after `base_moved` | `EnsureWorkBranchesAsync` reads the branch again, as `WorkHeadAsync` does | None (a race test would be needed) | — |

Design notes applied: LOG 190 (a prepared repository with no work branch yet has no raised revision; unreachable in std),
LOG 191 (3) wording (any added link is allowed, and proves nothing across runs), docs/04 section 10.2.

After the fixes: default **965 passed**, integration **78 passed**, 0 failed (Release, Temporal 9033). The ten controls
for the review hold, and those for LOG 190 and 198 after the helper moved.

### Clean-up

Deleted on both fixtures after the runs: every `ff/epic/*`, `ff/ticket/*`, `ff/project/*` and `ff/m6-live/*` branch and
every `refs/feature-flow/wip/*` reference the runs made (no `superseded` or `ci` reference was made). Listed afterwards:
each fixture has only `main`, no `refs/feature-flow/*`, no open pull request, no open issue and no workflow run in
progress or queued. The `[m6-live]` issues #176 to #179, made by the App for the epic, were closed by the run's tracker
updates and have a comment linking the run. Closed pull requests stay: #180 to #183 and #187 (an aborted attempt's, closed
when its branch was deleted) on the API fixture. Not removed: the labels `status: in progress` and `status: code review`
on the API fixture, the tracker's status labels, since W4's live contract may have made them first. No sandbox container
was left; hosts and the Temporal server were stopped.

### Tests run

On `m6@4ff0db2` before any change: default 954 passed, integration 72 passed. On `m6-integration` before the first
report: default **959 passed**, integration **77 passed**, 0 failed (Release, Temporal 9033). Controls: 16 in
`tools/check_m6_integration_controls.py` (24 after the review), each run when written; all hold.

### Deviations

- Done-when 2 ran `m6live:templates/project_delivery_imported` (run-only, not committed): std's `project_delivery` with
  `define` importing the person's documents, since M7's session is not built.
- People's recoveries were used where agents struggled (granted attempts to planners and reviewers, retried a
  conflict resolver); all are on the runs' timelines with the actor.
- `requirement_inheritance` now accepts added links (LOG 191 amended; accepted by the coordinator, for the product owner's review); docs/04 section 10.2 updated.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | A Docker-harness test that the agent's checkout holds its parent branch (LOG 193 extended is verified live only) |
| 2 | Check produced artifacts at report time with correction turns (LOG 176): haiku planners and reviewers lost many attempts to shapes and YAML the validators then refused. Planners still struggle with the three-level requirements model (one live ticket planner needed six granted attempts) |
| 3 | A person's `go_to` into a branch of a finished parallel re-runs the step but not the join, so the run ends `incomplete` (F9 holds). "Re-run this failed branch" is a recovery M7 should offer |
| 4 | Aborting a run does not close the pull requests it opened |
| 5 | A `run.child` start that fails leaves its budget reservation recorded as `starting` (LOG 187, known) |
| 6 | No live hand-over fell during a CI or merge wait (`InterruptedWaits` stayed 0): W1's `WaitHandOverTests` remain the evidence for LOG 139 |
| 7 | The hosts ignore SIGTERM and SIGINT when run detached, so stopping them needs SIGKILL |

---

## Coordinator decisions

Decisions the coordinator took during M6 (minor ones; the two major ones were the product owner's and are marked).

| # | Decision | Why |
|---|---|---|
| 1 | The shared interfaces (PR 9) were approved and frozen, including the code host's new keyed pull request comment (LOG 150) | W2 needed it to post decline reasons; adding it mid-build would have broken the freeze |
| 2 | W1 writes the artifact index (the record store's list of every artifact a run tree produced, LOG 155). A child reads its parent's and root's artifacts from its start input instead (LOG 153) | One writer; workflow code cannot read the store, and the start input pins "as at the child's start" |
| 3 | Fixed a timing flake in `GitCredentialRetryTests` (a 20 ms delay measured just under 20 ms). Control: the provider's delay removed made all three retry cases fail | It failed PR 9's CI and was not W0's change |
| 4 | Principle: the governed agent alone must not clear what blocks it. Only the verifier after a real fix, or a person, closes a blocking or demonstrated finding (LOG 168) | Review of PR 11 |
| 5 | Principle: acceptance criteria are met only by engine test evidence at the frozen head, or by a person, never by the agent's own classification (LOG 178) | Review of PR 13 |
| 6 | `delivery.report` counts a merge only if its head is the work branch's current head, it is not reverted, and the target still contains it. A deleted work branch is not later work (LOG 180) | Review and re-check of PR 12 |
| 7 | Links between requirements are compared by the document they resolve to, not by their sandbox name (LOG 200) | Review of PR 14 |
| 8 | **Product owner:** the GitHub App was granted Issues: read and write | The tracker and a run's ticket read need it |
| 9 | **Product owner:** the second fixture, `ai-feature-flow/feature-flow-example-client`, was created (public, ruleset `protect-main` copied from the first fixture). The product owner merged both project pull requests (#193, client #6), and the changes stay on both `main`s | Done-when 2 needs two repositories and a person's merges |

## For the product owner

LOG 148 to 200 are unreviewed. The entries whose "Needs a look?" column says yes, with a recommendation each:

| LOG | What it decided | Recommendation |
|---|---|---|
| 161 | `findings.triage` gains an `invalid` outcome, for a triage naming a finding the ledger doesn't hold. An action outcome added later escalates to a person when a plan doesn't route it, so older plans still load | Approve: the same compatibility rule will be needed for any later outcome |
| 164 | Decline reasons are posted only on an open pull request, so std's ticket flow (which triages before its pull request opens) posts none | Approve, with the follow-up: post the ledger's declines when the pull request opens |
| 168 | A triager agent may set only `open`, `declined` or `escalated`; only the verifier, after a commit, may set `fixed`. Escalations and declined blocking findings go to a person. The fix loop routes on engine counts | Approve: it closes the review's most serious hole |
| 171 | Test evidence records the names of passing tests (`TestResults.Passing`, a change to an interface frozen in M5) | Approve: claims can't be checked against what the engine saw without it |
| 175, 179 | std's `artifact.validate` steps keep their report on both outcomes; one rule for "the latest artifact of a name" | Approve |
| 176 | Checking produced artifacts against their types (schema, Markdown headings) is deferred | Approve the deferral, but schedule it first in M7: the live runs lost many attempts to it |
| 178 | Self-owned criteria count as unowned; `tests_added` needs a claimed test that passed in this run's results at the head, in a changed file that isn't the fixed file alone | Approve |
| 181, 185 | `artifact.import` checks only that files parse, not their schema; the ticket an agent sees has no issue comments | Approve as follow-ups |
| 186 | Declarative tracker updates give up after a few tries (about two minutes), so the issue's status can go stale but the run never stalls | Approve |
| 187 | The work-item budget: two runs creating children at the same instant can overshoot by one each; with no budget and no ceiling a tree is unlimited | Approve; set a ceiling in the shipped organisation profiles |
| 191 (3) | `requirement_inheritance` lets a child's copy keep every parent link and add its own (accepted by the coordinator) | Approve: the parent's meaning is kept, and it is what planners naturally write |
| 192 | std reviewers consume the specification they review against; whether to stage artifact types for agents generally is open | Approve; fold into the report-time checks (LOG 176) |
| 193 | An agent step's baseline is the merge base with its parent (`IVcsProvider.MergeBaseAsync`, an interface addition, accepted by the coordinator). The checkout also holds the parent branch | Approve |

## Consolidated follow-ups

| # | Follow-up | From |
|---|---|---|
| 1 | **First in M7:** check produced artifacts at report time, with correction turns (LOG 176; `docs/01` section 5.4). Haiku planners lost many attempts to it | Integration 2, Validators 2, Findings 1 |
| 2 | A person triages findings through a decision, recorded as the authenticated person (F8). Until then an escalation is answered with an intervention's recovery actions (LOG 168, 169) | Findings 7 |
| 3 | Recoveries: re-run a failed branch of a finished parallel step (today `go_to` re-runs the step but not the join, so the run ends `incomplete`); aborting a run closes its pull requests | Integration 3, 4 |
| 4 | **M4 12, still open:** the sandbox's network is unrestricted and reaches the Temporal frontend. Fix before any shared deployment | Earlier milestones |
| 5 | Post the ledger's decline reasons when `scm.pull_request` opens the pull request (LOG 164) | Findings 2 |
| 6 | A fixed rule vocabulary for std's reviewers, so a re-raise matches and `repeat_fixes` accrues | Findings 6 |
| 7 | `verification_satisfaction` doesn't check that a review accepted the claims; the trace doesn't check that a child completed | Validators 3, 4 |
| 8 | Schema checks on `artifact.import`; issue comments in the staged ticket; declarative `on.<exit>` through guarded routes and nested fragments | Delivery 1 to 3 |
| 9 | The work-item budget's concurrent-creation overshoot and a failed child start staying counted (LOG 187) | Delivery 4, Integration 5 |
| 10 | `EpicRunTests`' thin margin (one hand-over with a child running): raise the fake implementer's delay, or assert it from the timeline | Shell 1 |
| 11 | No live hand-over fell during a CI or merge wait, so `WaitHandOverTests` remain LOG 139's evidence | Integration 6 |
| 12 | The hosts ignore SIGTERM and SIGINT when run detached | Integration 7 |
| 13 | Copilot code review is off on the fixture, so the Copilot findings source has been seen only answering `unavailable` | Findings 4 |
| 14 | M5 follow-up 3 (a re-run reviewer isn't told which files it left unread) and M5 follow-up 7 (`test.integration` starting services) are still open | M5 |
