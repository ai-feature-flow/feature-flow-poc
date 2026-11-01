# M2: interpreter core and re-drive

Status: built 2026-11-02 on branch `m2-core`, and revised the same day after
review (section 9). Each std template has re-drive tests for its main path, its
correction loop, escalation on exhausted attempts and a lost merge race;
`project_delivery` also has partial delivery. Each run-time rule of `docs/09`
section 3 that the core owns has a test. Both controls the build plan names
were seen to fail and then pass after the revert, with eight more (section
2.4). GitHub CI has not run, for the reason in `docs/build/m0-results.md`
section 5.

## 1. What was built

| Project | Holds |
|---|---|
| `Interpreter` | `RunCore`, the pure core of `docs/02` section 2.1. `Advance` enters ready steps and returns commands (start a step, open a decision, raise an intervention, close a wait, cancel, clean up, end the run); `Apply` takes what happened (a step's result or failure, an answer, a patch, findings counts). It checks allowances, races and the run ceiling before every execution, clears a step's current execution on re-entry, routes with guards evaluated in CEL, runs `parallel` joins (`all`, `any`, `n`) and chains, and ends the run `incomplete` when nothing is left and no end route was reached. Patches go through one path that applies checks 22 and 25 to 30, with 3 and 17 on the patched plan (`Patches.cs`). `PlanExpansion` builds `plan.expand`'s patch from a list. No Temporal reference |
| `Testing.Workflows` | The workflow test file reader and the re-drive runner (`docs/09` section 4.3, extended by LOG 75) |
| `Cli` | `ff test <file or folder> [--library <folder>]... [--trace]` |
| `Library` | `LoadOptions` gains the run's repositories, so a `for_each` over `run.repositories` expands in full, and the patches applied so far. `PlanLoader.Patches.cs` applies patch operations to the expanded plan before routes are resolved |
| `Model` | The patch document and its reader (`Patch.cs`); check numbers 18, 22 and 25 to 30; a branch end carries a fragment exit's class |
| `Expressions` | `Evaluate` takes the typed paths the load check used |
| `library/std/tests/` | 18 workflow test files |

`ff test library/std/tests` prints `18 passed, 0 failed`.

## 2. Checks

### 2.1 Std's workflow tests

| Template | Main path | Correction loop | Exhausted attempts | Lost race | Other |
|---|---|---|---|---|---|
| `ticket_delivery` | `ticket_main_path` | `ticket_fix_loop`: one blocking finding; the first fix is rejected, the second verified; re-test and re-review | `ticket_fix_exhausted`: fixer runs 3 times, the 4th entry escalates | `ticket_base_moved`: 2 races lost, the second catch-up changes the branch, so re-test and checks run | `ticket_races_exhausted`: 10 lost races, then escalation; the merge's 3 attempts do not limit it (LOG 61). `ticket_spec_rejected`: the route back to the planner resets the branch (`clean_up`). `ticket_stale_output`: the second catch-up reports no `changed`, and the guard escalates instead of reading the first execution's `true` (DR12). `ticket_work_failures`: two work failures use two attempts, the third succeeds |
| `epic_delivery` | `epic_main_path`: `plan.expand` starts 2 child runs | `epic_fix_loop`: no satisfaction check at epic level | `epic_fix_exhausted`: `tests_added` never valid | `epic_base_moved` | `epic_child_failed`: one of two tickets fails; the join (`all`) reports `any_failed` and the epic escalates before verification |
| `project_delivery` | `project_main_path`: 2 epics, integration tests, ordered delivery to 2 repositories | `project_fix_loop`: requirements check fails once, epic plan fails once. The template has no findings fix loop | `project_plan_exhausted`: planner runs 3 times | `project_base_moved` | `project_partial_delivery`: the first merge is declined, the second repository's merge never starts, report partial, fix forward, status `partial` |

`WorkflowTestRunnerTests` runs each file, checks that the 17 required ones
exist, and shows the runner can fail: a wrong status, a path out of order, a
misspelt step id, an undeclared outcome, an unbounded loop, and a patch whose
blocking checks the file must name.

### 2.2 Run-time rules of `docs/09` section 3

| Rule | Owned by the core? | Test |
|---|---|---|
| Attempt ceilings on every execution (F4) | Yes | `Every_entry_counts_against_the_allowance_and_the_entry_after_the_last_is_refused`, `The_documents_default_limit_sets_the_allowance`, `The_run_ceiling_stops_a_loop_across_steps`, the two self-loop tests below; std's three `*_exhausted` tests and `ticket_races_exhausted` |
| Re-entry runs a step again | Yes | `Reentry_clears_the_current_execution_and_keeps_the_previous_readable`, `A_loop_guard_reads_the_new_execution_and_a_number_given_as_a_double_evaluates_as_checked`, std's `ticket_stale_output` |
| No success without an end route (F9) | Yes | `An_unrouted_failure_ends_the_run_incomplete_not_completed`, `Only_an_end_route_completes_a_run` |
| Output contracts | In part: the core refuses an outcome the step does not have, an undeclared output, a value of the wrong type (a fractional number anywhere, LOG 76), a declared output missing from a `success` report (LOG 79), a missing or undeclared artifact, and an agent's invalid patch. Correction turns are the agent step's (M4) | `An_outcome_the_step_does_not_have_is_refused`, `An_undeclared_output_and_a_fractional_number_are_refused`, `A_fraction_in_a_list_or_in_an_unlisted_action_output_is_refused`, `A_declared_output_must_be_reported_on_a_success_outcome`, `A_promised_artifact_must_be_produced_and_no_other`, `Check_25_a_stale_base_revision_is_rejected_as_a_contract_error_without_using_an_attempt` |
| F1 at merge time | No: `scm.merge` (M5) | |
| Unmapped success is a work error | No: providers (M5) | |
| Evidence integrity (F7) | No: engine actions (M6). Load check 12 refuses an agent or decision that declares an evidence type; on a patch that is check 30 (LOG 78) | |
| Actor from identity (F8) | No: the API (M3, M7). The core takes a patch's proposer from the reporting step's form, or from the shell for a patch from outside a step, never from the document | `A_planner_claiming_to_be_a_person_is_still_an_agent` |

Further core tests:
- infrastructure failures are retried without using an attempt; work failures use one;
- a guard that cannot be evaluated raises an intervention;
- an exhausted step routed back into itself escalates within one `Advance`, by its allowance and by the run ceiling (each test fails if `Advance` has not returned in 5 seconds);
- `clean_up` is emitted only when the step will run;
- a fractional `number` parameter is refused at run start (check 18);
- a waiting branch starts when a running one ends;
- joins `all`, `any` (cancels the rest, closes open decisions) and `n`;
- a chain stops at its first failure;
- an open decision does not block other steps.

### 2.3 Patch checks

Each test proposes a patch to a running probe template and asserts the
blocking check numbers (`PatchTests`).

| Check | Patch | Result |
|---|---|---|
| 3 | A step with an existing id; a step reusing the id of one an earlier patch removed | Rejected, 3 |
| 17 | 2,001 branches added to a `parallel` | Rejected, 17 |
| 22 | An allowance above the per-step cap | Rejected, 22 |
| 25 | `base_revision` 2 when the plan is at 1 | Refused as a contract error; the planner stays running and uses no attempt |
| 26 | `resume_at` names the step the patch removes | Rejected, 26 |
| 27 | Proposed by a step without planning authority; a `replace` that changes a role | Rejected, 27 |
| 28 | A route to a step that does not exist; removing a `parallel` whose branches are running | Rejected, 28 (inner check 8); rejected, 28 |
| 29 | An agent's `set` on a protected step; a role outside `planning.allowed_roles`; a planner claiming `proposed_by: human` to remove a protected step | Pending; approved, the patch applies and the run resumes |
| 30 | An agent step with `write_access: repository` on a reviewer role | Rejected, 30 |

Applied patches: `insert` before and after, `chain`, `set` of a route,
`remove` by a person on a protected step, `add_branch` before a `parallel`
starts and refused after. An approved patch that has gone stale is rejected
(25) and the proposer routes on. `plan.expand` gives the same patch for the
same list, chains steps after itself, and goes straight on for an empty list.

### 2.4 Controls

`tools/check_m2_controls.py` breaks each rule in the source, runs the tests
that guard it, restores the source and runs them again. It exits 0 only when
each control's named tests fail while broken and every test passes after the
revert. It did, on the revised code:

| Control | Tests failing while broken | After revert |
|---|---|---|
| Allowance check removed (F4). The build plan's first control | `ticket_fix_exhausted`, `epic_fix_exhausted`, `project_plan_exhausted`, and four core tests. On the first version, `ticket_fix_exhausted` reported `verify.fix.fix ran 91 time(s), expected 3`: the loop ran until the run ceiling of 200 attempts stopped it | All pass |
| End-route rule removed (F9): a run with nothing left reports `completed`. The build plan's second control | `An_unrouted_failure_ends_the_run_incomplete_not_completed`: `Expected: Incomplete, Actual: Completed` | All pass |
| Routing without progress not detected (review B1) | Both self-loop tests: `the core did not return within 5 seconds` | All pass |
| Race allowance removed (LOG 61) | `ticket_races_exhausted` | All pass |
| Re-entry keeps the current execution (DR12) | `ticket_stale_output`, `Reentry_clears_...`, `A_loop_guard_reads_the_new_execution_...` | All pass |
| A chain goes on after an item fails | `project_partial_delivery`, `A_chain_runs_items_in_order_and_stops_...` | All pass |
| Join `all` ignores a failed branch | `epic_child_failed` | All pass |
| No `CleanUp` command emitted | `ticket_spec_rejected` | All pass |
| Numbers not converted to whole numbers (LOG 76) | Four core tests, including the run-start parameter test | All pass |
| `Temporalio` referenced from `Testing.Workflows` | `Every_project_under_src_and_hosts_obeys_the_reference_rules` | All pass |

### 2.5 Counts

The default category has 226 tests, up from M1's 150: `Validation.Tests` 57,
`Expressions.Tests` 48, `Interpreter.Tests` 44, `Testing.Workflows.Tests` 26
(18 std files and 8 runner tests), `Architecture.Tests` 20, `Cli.Tests` 15,
`Model.Tests` 11, `Library.Tests` 5. The integration category has 2.

## 3. Errors found in std

None. All 18 workflow tests pass against std as M1 left it. Std's routing,
allowances and fragment exits behaved as the tests expect on every path
listed in section 2.1.

## 4. Design changes

LOG 75 to 80:

| LOG | Status | Change |
|---|---|---|
| 75 | Unreviewed | Workflow test file additions: `expand`, error entries, `~` for an output not reported, `expect.executions`, `expect.escalated`, `expect.clean_up`, and the meaning of a visit for lists, namespace values and unlisted outputs. Marked pending review in `docs/09` section 4.3 |
| 76 | Approved 2026-11-02 | A `number` is a whole number at run time; a fractional value is refused. `docs/03` section 5.4 now says so |
| 77 | Unreviewed | A step's limits default from its own document's `defaults.limits`, never the caller's, as LOG 72 says for routes |
| 78 | Unreviewed | Patch details `docs/03` section 10.2 leaves open: the scope of supplied steps, `insert` and `replace` routing, `remove_branch`, the engine's operations, `plan.expand` ids, who the proposer is, and which check number reports what (an allowance above a cap is check 22) |
| 79 | Approved 2026-11-02; its scope is unreviewed | A declared output a step does not report is an output contract error. The core enforces it on `success` outcomes, as a `produces` list applies to them. `docs/03` section 5.4 and `docs/04` section 3.3 updated; correction turns arrive in M4 |
| 80 | Unreviewed | `Testing.Workflows` may reference only the core, and no Temporal package. Added to the table in `docs/11` section 2.3 |

LOG 75, 77, 78 and 80 fill gaps or tighten an unconstrained entry. Each was
checked against `docs/01`, `03`, `04`, `09` and `11` and against LOG 1 to 74,
and none contradicts an approved rule.

## 5. Implementation choices

None changes the design beyond the LOG entries above.

| Choice | Reason |
|---|---|
| A guard is evaluated with each `steps.` path bound as a qualified variable with the type the load check gave it (`steps.triage.outputs.to_fix` as int) | A guard type-checked at load then evaluates under the same types. Cel.NET resolves the qualified name before the select chain, as at load |
| Every number reaching the core is converted to a whole number or refused (LOG 76): declared outputs, items of lists, values in maps, outputs of actions whose outputs `docs/04` does not list, and parameters. A literal parameter is checked at run start (check 18); one given by an expression when it is read | A probe showed the need: `to_fix + 1 == 3` with `to_fix` given as 2.0 fails with `no such overload: double.add(int)` |
| Within one `Advance`, a step entered twice before any step starts or any wait opens raises an intervention at that step | Routing without running anything can only repeat: an exhausted step routed back into a cycle, by its allowance or by the run ceiling. No load check applies: check 10 does not count an `attempts_exhausted` route as a route out (LOG 74), and the reviewer's self-loop has a real route out (`passed` ends the run) |
| A patch reported by a step is proposed by the step's form; a patch from outside a step carries the proposer the shell names (`PatchProposed.By`) | The document's `proposed_by` is written by whoever wrote the patch, so it cannot be trusted (F8) |
| `remove` and `replace` are refused (check 28) while the target, or any step inside it, is running, waiting or queued, or the target `parallel` is entered. The proposing step itself does not count | A removed `parallel` with running branches left the run unable to settle |
| Plan revision n is the template loaded again with the first n - 1 patches | Revisions stay immutable without cloning the plan, and a patched plan gets defaults, routing and every load check exactly as a written one does (check 28) |
| Patch operations are applied in `Library` (`PlanLoader.Patches.cs`), between expansion and route resolution; the checks and the decision to apply are in `Interpreter` | Route resolution and lane order live in the loader. Applying operations there avoids a second resolver |
| Engine defaults in `RunOptions`: 3 attempts, 10 races, a run ceiling of 200 attempts, no per-step cap, no auto-resolution | The design leaves these to installation and organisation configuration (`docs/07` sections 2 and 3), which M3 and M7 bring |
| `scm.merge` charges an execution to `limits.races` when it ends `base_moved` and to its attempts otherwise; both are checked before every execution | `docs/04` section 7.3: attempts are "used by executions that end other than `base_moved`". With 10 races the merge runs at most 10 times before the 11th entry escalates |
| The run ceiling counts every started execution of an agent step or action, including merges | `docs/01` section 4: "a ceiling on all attempts" |
| A route guard that cannot be evaluated (an output not reported, a step not yet run) raises an intervention | `docs/04` section 1, "cannot answer": an intervention instead of a guessed outcome |
| An agent's rejected patch is refused like any contract error: nothing is recorded and no attempt is used. A rejected `plan.expand` patch raises an intervention | `docs/03` section 10.3 for agents; section 10.4 says a valid list cannot fail, so a rejection needs a person |
| A join that has decided (`any`, `n`) cancels its other branches and closes their open decisions and interventions (`CloseWait`); an end route does the same for the whole run | `docs/03` section 8.1: "the others are cancelled" |
| `clean_up` is emitted after the allowance check, only when the step will run | A reset for a step that then does not run would discard work for nothing |
| Each route evaluation reads each `findings` count once, through `IFindingsSource` | The merge bar reads `open_blocking` in two guards; both must see one value |
| A parameter is evaluated at each read, not cached | A fragment binding may read a step's output, which changes when the step runs again |
| The re-drive runner finishes one step at a time, in start order, and stops after 5,000 step results | Deterministic paths; an unbounded loop fails the test instead of hanging it |
| Messages format numbers with the invariant culture; `IdHistory` is an ordinal sorted set | Replay must not depend on the machine's culture or on hash order |
| `ff test --trace` prints each run's timeline | Debugging a re-drive test needs the path the core took |

## 6. CI

To be recorded after the clean-clone run.

## 7. Open questions

| # | Question |
|---|---|
| 1 | LOG 79: should a declared output be required on every outcome, or only on `success` outcomes as built? A blocked planner cannot know its `tier_tags` |
| 2 | LOG 75, 77, 78 and 80 need review |

## 8. Follow-ups

Not done in M2:

| # | Follow-up |
|---|---|
| 1 | Answers to interventions (retry, go to, grant attempts, end) are not in the core. An escalation stays open; M3 brings answers as Temporal Updates |
| 2 | Breakpoints, auto-resolution policy, allowed roles and caps come from organisation configuration in M7; `RunOptions` stands in |
| 3 | A patch sent to a parent (`docs/03` section 9.4) and `resume_at` omitted for it are not handled; they need child runs (M3) |
| 4 | Check 29 does not detect a patch that makes a protected step unreachable without touching it; such a patch fails check 9 instead and is rejected, not made pending |
| 5 | `clean_up` is emitted as a command and recorded; resetting the branch is the VCS provider's (M5) |
| 6 | Run state is not yet serialised for continue-as-new (M3) |
| 7 | Correction turns for output contract errors and invalid patches arrive with the agent step (M4); until then a refused report leaves the step running |

## 9. Review changes

Requested by the coordinator's review of `ed254c4`, 2026-11-02, with two
product owner decisions (LOG 76 approved; LOG 79):

| # | Change |
|---|---|
| B1 | An exhausted step routed back into a cycle looped forever inside `Advance`. A step entered twice without progress now escalates (section 5). Tests with a 5-second timeout, for the allowance and the run ceiling |
| B2 | A patch's proposer came from the document. It now comes from the reporting step's form, or from the shell. `A_planner_claiming_to_be_a_person_is_still_an_agent` |
| B3 | Removing a `parallel` with running branches was applied and the run never settled. Every step inside the target is checked. `Removing_a_parallel_whose_branches_are_running_is_rejected` |
| B4 | LOG 76 is enforced for parameters at run start (check 18), list items, map values and unlisted action outputs |
| 1 | `{ error: work, times, then }` now fails `times` executions, then takes `then`. `ticket_work_failures` |
| 2 | Std tests now guard the chain stop, a failed join, `clean_up` and DR12, each with a control (section 2.4) |
| 3 | `clean_up` is emitted after the exhaustion check |
| 4, 8 | The cap check uses the same sources as `RaceAllowance` and `Allowance`, and reports check 22. LOG 78 amended |
| 5 | An agent's rejected patch is a contract error and uses no attempt |
| 6 | A decided join closes its branches' open waits with `CloseWait`; so does the end of the run |
| 7 | Invariant culture in messages; `IdHistory` is a sorted set; the state comment corrected |
| 9 | `Testing.Workflows` rule recorded in `docs/11` section 2.3 and LOG 80 |
| Also | An approved patch that has gone stale lets the proposer route on, instead of stranding it |
