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
