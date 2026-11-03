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
