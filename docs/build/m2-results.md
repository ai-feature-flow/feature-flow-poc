# M2: interpreter core and re-drive

Status: built 2026-11-02 on branch `m2-core`. Each std template has re-drive
tests for its main path, its correction loop, escalation on exhausted
attempts and a lost merge race; `project_delivery` also has partial delivery.
Each run-time rule of `docs/09` section 3 that the core owns has a test. Both
controls the build plan names were seen to fail and then pass after the
revert, with four more (section 2.4). GitHub CI has not run, for the reason in
`docs/build/m0-results.md` section 5.

## 1. What was built

| Project | Holds |
|---|---|
| `Interpreter` | `RunCore`, the pure core of `docs/02` section 2.1. `Advance` enters ready steps and returns commands (start a step, open a decision, raise an intervention, cancel, clean up, end the run); `Apply` takes what happened (a step's result or failure, an answer, a patch, findings counts). It checks allowances, races and the run ceiling before every execution, clears a step's current execution on re-entry, routes with guards evaluated in CEL, runs `parallel` joins (`all`, `any`, `n`) and chains, and ends the run `incomplete` when nothing is left and no end route was reached. Patches go through one path that applies checks 25 to 30, with 3 and 17 on the patched plan (`Patches.cs`). `PlanExpansion` builds `plan.expand`'s patch from a list. No Temporal reference |
| `Testing.Workflows` | The workflow test file reader and the re-drive runner (`docs/09` section 4.3, extended by LOG 75) |
| `Cli` | `ff test <file or folder> [--library <folder>]... [--trace]` |
| `Library` | `LoadOptions` gains the run's repositories, so a `for_each` over `run.repositories` expands in full, and the patches applied so far. `PlanLoader.Patches.cs` applies patch operations to the expanded plan before routes are resolved |
| `Model` | The patch document and its reader (`Patch.cs`); check numbers 25 to 30; a branch end carries a fragment exit's class |
| `Expressions` | `Evaluate` takes the typed paths the load check used |
| `library/std/tests/` | 14 workflow test files |

`ff test library/std/tests` prints `14 passed, 0 failed`.

## 2. Checks

### 2.1 Std's workflow tests

| Template | Main path | Correction loop | Exhausted attempts | Lost race | Other |
|---|---|---|---|---|---|
| `ticket_delivery` | `ticket_main_path` | `ticket_fix_loop`: one blocking finding; the first fix is rejected, the second verified; re-test and re-review | `ticket_fix_exhausted`: fixer runs 3 times, the 4th entry escalates | `ticket_base_moved`: 2 races lost, the second catch-up changes the branch, so re-test and checks run | `ticket_races_exhausted`: 10 lost races, then escalation; the merge's 3 attempts do not limit it (LOG 61) |
| `epic_delivery` | `epic_main_path`: `plan.expand` starts 2 child runs | `epic_fix_loop`: no satisfaction check at epic level | `epic_fix_exhausted`: `tests_added` never valid | `epic_base_moved` | |
| `project_delivery` | `project_main_path`: 2 epics, integration tests, ordered delivery to 2 repositories | `project_fix_loop`: requirements check fails once, epic plan fails once. The template has no findings fix loop | `project_plan_exhausted`: planner runs 3 times | `project_base_moved` | `project_partial_delivery`: second merge declined, report partial, fix forward, status `partial` |

`WorkflowTestRunnerTests` runs each file, checks that the 13 required ones
exist, and shows the runner can fail: a wrong status, a path out of order, a
misspelt step id, an undeclared outcome, an unbounded loop, and a patch whose
blocking checks the file must name.

### 2.2 Run-time rules of `docs/09` section 3

| Rule | Owned by the core? | Test |
|---|---|---|
| Attempt ceilings on every execution (F4) | Yes | `Every_entry_counts_against_the_allowance_and_the_entry_after_the_last_is_refused`, `The_documents_default_limit_sets_the_allowance`, `The_run_ceiling_stops_a_loop_across_steps`; std's three `*_exhausted` tests and `ticket_races_exhausted` |
| Re-entry runs a step again | Yes | `Reentry_clears_the_current_execution_and_keeps_the_previous_readable`, `A_loop_guard_reads_the_new_execution_and_a_number_given_as_a_double_evaluates_as_checked` |
| No success without an end route (F9) | Yes | `An_unrouted_failure_ends_the_run_incomplete_not_completed`, `Only_an_end_route_completes_a_run` |
| Output contracts | In part: the core refuses an outcome the step does not have, an undeclared output, a value of the wrong type, and a missing or undeclared artifact. Correction turns are the agent step's (M4) | `An_outcome_the_step_does_not_have_is_refused`, `An_undeclared_output_and_a_fractional_number_are_refused`, `A_promised_artifact_must_be_produced_and_no_other` |
| F1 at merge time | No: `scm.merge` (M5) | |
| Unmapped success is a work error | No: providers (M5) | |
| Evidence integrity (F7) | No: engine actions (M6). Load check 12 refuses an agent or decision that declares an evidence type; on a patch that is check 30 (LOG 78) | |
| Actor from identity (F8) | No: the API (M3, M7) | |

Further core tests: infrastructure failures are retried without using an
attempt and work failures use one; a guard that cannot be evaluated raises an
intervention; a waiting branch starts when a running one ends; joins `all`,
`any` (cancels the rest) and `n`; a chain stops at its first failure; an open
decision does not block other steps.

### 2.3 Patch checks

Each test proposes a patch to a running probe template and asserts the
blocking check numbers (`PatchTests`).

| Check | Patch | Result |
|---|---|---|
| 3 | A step with an existing id; a step reusing the id of one an earlier patch removed | Rejected, 3 |
| 17 | 2,001 branches added to a `parallel` | Rejected, 17 |
| 25 | `base_revision` 2 when the plan is at 1 | Rejected, 25; the planner's attempt fails and it runs again |
| 26 | `resume_at` names the step the patch removes | Rejected, 26 |
| 27 | Proposed by a step without planning authority; a `replace` that changes a role | Rejected, 27 |
| 28 | A route to a step that does not exist | Rejected, 28 (inner check 8) |
| 29 | An agent's `set` on a protected step; a role outside `planning.allowed_roles` | Pending; approved, the patch applies and the run resumes |
| 30 | An agent step with `write_access: repository` on a reviewer role; an allowance above the per-step ceiling | Rejected, 30 |

Applied patches: `insert` before and after, `chain`, `set` of a route,
`remove` by a person on a protected step, `add_branch` before a `parallel`
starts and refused after. `plan.expand` gives the same patch for the same list,
chains steps after itself, and goes straight on for an empty list.

### 2.4 Controls

`tools/check_m2_controls.py` breaks each rule in the source, runs the tests
that guard it, restores the source and runs them again. It exits 0 only when
each control's named tests fail while broken and every test passes after the
revert. It did, on the committed code:

| Control | Tests failing while broken | After revert |
|---|---|---|
| Allowance check removed (F4). The build plan's first control | `ticket_fix_exhausted`, `epic_fix_exhausted`, `project_plan_exhausted`, and two core tests. `ticket_fix_exhausted` reports `verify.fix.fix ran 91 time(s), expected 3`: the loop ran until the run ceiling of 200 attempts stopped it | All pass |
| End-route rule removed (F9): a run with nothing left reports `completed`. The build plan's second control | `An_unrouted_failure_ends_the_run_incomplete_not_completed`: `Expected: Incomplete, Actual: Completed` | All pass |
| Race allowance removed (LOG 61) | `ticket_races_exhausted` | All pass |
| Re-entry keeps the current execution (DR12) | `Reentry_clears_...`, `A_loop_guard_reads_the_new_execution_...` | All pass |
| Number outputs not converted (LOG 76) | `A_loop_guard_..._a_number_given_as_a_double_...`, `An_undeclared_output_and_a_fractional_number_are_refused` | All pass |
| `Temporalio` referenced from `Testing.Workflows` | `Every_project_under_src_and_hosts_obeys_the_reference_rules` | All pass |

### 2.5 Counts

The default category has 212 tests, up from M1's 150: `Validation.Tests` 57,
`Expressions.Tests` 48, `Interpreter.Tests` 34, `Testing.Workflows.Tests` 22
(14 std files and 8 runner tests), `Architecture.Tests` 20, `Cli.Tests` 15,
`Model.Tests` 11, `Library.Tests` 5. The integration category has 2.

## 3. Errors found in std

None. All 14 workflow tests passed against std as M1 left it. Std's routing,
allowances and fragment exits behaved as the tests expect on every path
listed in section 2.1.

## 4. Design changes

LOG 75 to 78, unreviewed:

| LOG | Change |
|---|---|
| 75 | Workflow test file additions: `expand`, error entries, `expect.executions`, `expect.escalated`, and the meaning of a visit for lists, namespace values and unlisted outputs. Marked pending review in `docs/09` section 4.3 |
| 76 | **Needs a decision.** A `number` output or parameter holds a whole number at run time; a fractional value is refused. Section 5.4 says `number` without saying whole |
| 77 | A step's limits default from its own document's `defaults.limits`, never the caller's, as LOG 72 says for routes |
| 78 | Patch details `docs/03` section 10.2 leaves open: the scope of supplied steps, `insert` and `replace` routing, `remove_branch`, the engine's operations, `plan.expand` ids, and which check number reports what |

LOG 76 narrows a type that the approved `docs/03` section 5.4 allows; whether
that overturns 5.4 is for the product owner. `docs/03` is left unchanged.
LOG 75, 77 and 78 fill gaps: each was checked against `docs/01`, `03`, `04` and
`09` and against LOG 1 to 74, and none contradicts an approved rule.

## 5. Implementation choices

None changes the design, so none has a LOG entry.

| Choice | Reason |
|---|---|
| `Testing.Workflows` may reference only the core projects, and no `Temporalio` package: a new row in the reference-rule table (`tests/FeatureFlow.Architecture.Tests/ReferenceRules.cs`) | LOG 66 says re-drive runs without Temporal. `docs/11` section 2.3 gave the project no rule. With `<PackageReference Include="Temporalio" />` added to its project file, the real-file test fails with `FeatureFlow.Testing.Workflows references package Temporalio: re-drive runs without Temporal (LOG 66)` |
| A guard is evaluated with each `steps.` path bound as a qualified variable with the type the load check gave it (`steps.triage.outputs.to_fix` as int) | A guard type-checked at load then evaluates under the same types. Cel.NET resolves the qualified name before the select chain, as at load |
| A `number` value reported as a double with a whole value is stored as an integer; a fractional one is refused (LOG 76) | A fractional `number` could reach a guard only through a step's output or a parameter. Both pass through `Values.Convert`, so neither keeps a double. A probe showed the need: `to_fix + 1 == 3` with `to_fix` given as 2.0 fails with `no such overload: double.add(int)` |
| Plan revision n is the template loaded again with the first n - 1 patches | Revisions stay immutable without cloning the plan, and a patched plan gets defaults, routing and every load check exactly as a written one does (check 28) |
| Patch operations are applied in `Library` (`PlanLoader.Patches.cs`), between expansion and route resolution; the checks and the decision to apply are in `Interpreter` | Route resolution and lane order live in the loader. Applying operations there avoids a second resolver |
| Engine defaults in `RunOptions`: 3 attempts, 10 races, a run ceiling of 200 attempts, no per-step ceiling, no auto-resolution | The design leaves these to installation and organisation configuration (`docs/07` sections 2 and 3), which M3 and M7 bring |
| `scm.merge` charges an execution to `limits.races` when it ends `base_moved` and to its attempts otherwise; both are checked before every execution | `docs/04` section 7.3: attempts are "used by executions that end other than `base_moved`". With 10 races the merge runs at most 10 times before the 11th entry escalates |
| The run ceiling counts every started execution of an agent step or action, including merges | `docs/01` section 4: "a ceiling on all attempts" |
| A route guard that cannot be evaluated (an output not reported, a step not yet run) raises an intervention | `docs/04` section 1, "cannot answer": an intervention instead of a guessed outcome |
| An agent's rejected patch fails its attempt, and the step runs again within its allowance; a rejected `plan.expand` patch raises an intervention | `docs/01` section 3.2 for agents; `docs/03` section 10.4 says a valid list cannot fail, so a rejection needs a person |
| A join that has decided (`any`, `n`) cancels its other branches; an end route cancels everything still running | `docs/03` section 8.1: "the others are cancelled" |
| Each route evaluation reads each `findings` count once, through `IFindingsSource` | The merge bar reads `open_blocking` in two guards; both must see one value |
| A parameter is evaluated at each read, not cached | A fragment binding may read a step's output, which changes when the step runs again |
| The re-drive runner finishes one step at a time, in start order, and stops after 5,000 step results | Deterministic paths; an unbounded loop fails the test instead of hanging it |
| `ff test --trace` prints each run's timeline | Debugging a re-drive test needs the path the core took |

## 6. CI

To be recorded after the clean-clone run.

## 7. Open questions

| # | Question |
|---|---|
| 1 | LOG 76: may a `number` output be fractional? If yes, how should a guard compare it with an integer literal, given the checker cannot declare int and double equality? LOG 75, 77 and 78 need review |
| 2 | Is an agent's declared output required when it reports? The core accepts a report without one; a guard that later reads it raises an intervention |

## 8. Follow-ups

Not done in M2:

| # | Follow-up |
|---|---|
| 1 | Answers to interventions (retry, go to, grant attempts, end) are not in the core. An escalation stays open; M3 brings answers as Temporal Updates |
| 2 | Breakpoints, auto-resolution policy and allowed roles come from organisation configuration in M7; `RunOptions` stands in |
| 3 | A patch sent to a parent (`docs/03` section 9.4) and `resume_at` omitted for it are not handled; they need child runs (M3) |
| 4 | Check 29 does not detect a patch that makes a protected step unreachable without touching it; such a patch fails check 9 instead and is rejected, not made pending |
| 5 | `clean_up` is emitted as a command and recorded; resetting the branch is the VCS provider's (M5) |
| 6 | Run state is not yet serialised for continue-as-new (M3). Its collections are ordered so that it can be |
