# Alpha follow-ups results

Six follow-ups to alpha (milestones A1 to A6 of `docs/12-build-plan-phase-2.md`), built 2026-11-22 from the follow-up
lists of `docs/build/a6-results.md` and `docs/build/a4-results.md` and one race seen on CI. Four builders worked in
parallel, an independent reviewer read the combined branch, and one fix pass closed its findings. Keys that loaded and
did nothing are now refused; check 31 runs at step start for every action; the merge-action race is fixed in the
workflow shell with a test that forces it. Judgement calls are LOG 487 to 495, unreviewed. No replay history changed.

## What was built

| # | Item | Built | LOG |
|---|---|---|---|
| 1 | Refuse keys that load and do nothing (A6 follow-ups 1 and 2; LOG 472, 473) | A library manifest's `inputs`, `resources` and `notifications`, a dependency's `version`, a role's `requires`, a repository manifest's `library` section and a version constraint inside a library reference (`acme@>=2.0.0:tasks/x`) are refused at load with "`<key>` is not supported in version 1; remove it". Their fields and code are deleted; check 20, which only checked the repository `library` section, is retired and its number not reused. `requires.engine`, `plan_schema` and `capabilities` stay: run start applies them. std, the test libraries and the k3d smoke library drop the keys. `docs/01`, `03`, `07`, `08` and `09` say they are refused | 487 |
| 2 | Step-start check 31 for `run.child` and `plan.expand` (A6 follow-up 8; LOG 483) | The core checks every action's evaluated inputs at step start (`RunCore.StartInputs`), so `run.child` and `plan.expand` take the same path as engine actions; a refused child is never created. The engine activity's own check is deleted: the core is the one place. `plan.expand` uses the inputs the check passed rather than evaluating them again | 488, 491 |
| 3 | `wait.condition`'s needs from its condition (A6 follow-up 9) | An action input may carry needs added when a step sets it; check 21 asks for them. `pull_request` needs a code host, `checks` a code host's check status, `tracker` a tracker | 489 |
| 4 | Descriptor outputs (A6 follow-up 10) | `repo.inspect`, `tracker.update` and `delivery.report` declare the outputs their handlers publish, so check 5 refuses a reference to any other | 490 |
| 5 | Merge-action decision race (CI run 44886500613 on `2e69534`) | Cause: an `approve` cancels the decision's watch and restarts the **same** execution; the cancelled watch's last "waiting" failure came back with retry state `CancelRequested`, missed the wake-only catch, and was applied to the restarted execution as a step failure, so a second decision opened and the person's merge was recorded as a bypass. Fix: in `RunWorkflow.RunStepAsync`, an activity the core cancelled no longer speaks for its step (known by its cancellation token), and `CancelStep` clears a wake's claim on it. The core cannot tell two activities of one execution apart; the shell already identifies them by token | 492 |
| 6 | A4 defects (A4 follow-ups 2, 4, 5) | `GET /runs/{id}/plan` answers 204 before the first plan revision; the console shows "No plan to show yet." and `ff run plan` says so. A host's start-up refusal (`StartupRefusal`) is its message alone on standard error and exit 2, in all three hosts. `POST /runs` runs the interpreter's own check 18 and refuses with 400 naming each missing or mistyped parameter | 493 to 495 |
| Also | Test isolation | The git library source takes a temporary root, so `GitLibrarySourceTests.Nothing_is_left_behind_...` no longer races other processes on the shared temp folder (A4 report) | None |

## Checks

| Done when | Test |
|---|---|
| A load test per key naming it; std loads clean | `OrganisationTests.A_manifest_key_that_does_nothing_is_refused_naming_it` (`inputs`, `resources`, `notifications`, `dependencies.version`), `A_roles_requires_is_refused_naming_it`; `ManifestTests.A_manifest_that_is_wrong_is_refused_not_ignored` (two `library` cases); `YamlReadingTests.Malformed_references_do_not_parse` and `StdLibraryTests.A_version_constraint_in_a_templates_reference_is_refused_naming_it`; `StdLibraryTests.Std_validates_with_no_diagnostics` |
| A test per action where an input valid at load resolves to an invalid value at step start and is refused | `ShellToCoreTests.A_run_child_step_whose_inputs_evaluate_outside_its_contract_cannot_start_its_child`, `A_plan_expand_step_whose_inputs_evaluate_outside_its_contract_cannot_start`, and the engine actions' theory `An_engine_actions_inputs_outside_its_contract_at_step_start_stop_the_step_before_it_runs`; at the shell, `ChildRunTests.A_run_child_step_whose_inputs_evaluate_outside_its_contract_asks_a_person_and_starts_no_child` (Integration); `PatchTests.Plan_expand_builds_with_the_inputs_checked_at_step_start_not_evaluated_again` |
| Check 21 tests for each condition kind | `RunStartCheckTests.Check_21_wait_condition_needs_the_provider_its_condition_reads_and_no_other` (`pull_request`, `checks`, `tracker`) |
| A check 5 test per action refusing an output it does not have | `MutationTests.A_guard_reading_an_output_its_action_does_not_publish_is_check_5` (`repo.inspect`, `tracker.update`, `delivery.report`) |
| A deterministic test that reproduces the ordering and fails before the fix; the Integration test 20 in a row | `MergeActionDecisionTests.An_approval_while_the_watch_reads_the_pull_request_lets_the_same_execution_ask_a_person_to_merge` holds the watch's read across the approval (`InMemoryForge.HoldReads`): failed 3 of 3 on the base engine, passes with the fix. `A_read_that_finds_the_merge_after_the_approval_cancelled_it_does_not_speak_for_the_step` and `A_wake_that_cancelled_the_watch_before_the_approval_did_does_not_outlive_it` force the other two orderings. `MergeActionDecisionTests` passed 20 runs in a row after the fix, and 5 in a row five times after the fix pass |
| A test each for the A4 defects; no console error on a new run | `ApiTests.A_run_s_plan_is_shown_only_to_a_viewer_of_the_run`; `RunPageTests.A_new_run_with_no_plan_revision_yet_is_refused_nothing` (Playwright; `ExpectNoRefusal` makes a refused request an error, which the test base had admitted); `RunCommandTests.Plan_says_when_the_run_has_no_revision_yet`; `StartupRefusalTests` (each host process exits 2 with one line; it exited 134 before); `StartRunTests.A_start_missing_required_parameters_is_refused_naming_each`, `A_literal_of_the_wrong_type_is_refused`, `A_start_with_every_required_parameter_is_accepted` |

## Controls

Each named test was seen to fail with the code it guards broken, then pass restored: by the builders and the fix pass
(about 25 checks, in their reports), by the reviewer (eight, two of which found unguarded code: the shell's handling of
a refused child, and the race fix's `!current` guard alone; both are now tested), and by the sub-orchestrator on the
merged branch (three: `notifications` no longer refused, the core's check 31 disabled, `POST /runs`' check 18
disabled; 1, 12 and 2 tests failed respectively).

## Verification

| Suite | Result at the head |
|---|---|
| Default (`Category!=Integration&Category!=Live&Category!=Cluster`) | 1,804 passed, 0 failed, 25 projects (A6: 1,775) |
| Integration category (local Temporal dev server) | 189 passed, 0 failed: Integration.Tests 128, Postgres 30, Docker 17, S3 12, Api 2 |
| Replay | 44 passed; no history re-recorded |

## Review

An independent reviewer read `41f752c..5d072e1`: no high findings, one medium (no test guarded the shell's handling of
a `run.child` refused by check 31; ignoring it would have null-referenced the workflow), three low (the race fix's
`!current` guard untested alone; `plan.expand` evaluating its inputs a second time; LOG 487's Where column). All four
were fixed, with the two planned items (a reference's version constraint refused; the temp-folder race).

## Deviations and judgement calls

LOG 487 to 495, unreviewed. Worth a look: LOG 495 (`POST /runs` refuses check 18 only; other run-start checks stay the
engine's), and LOG 492 (the race fixed in the workflow shell rather than the core, since the core cannot tell two
activities of one execution apart). The deterministic race tests are Integration tests, as the race needs a real
activity cancel. The k3d smoke library under `deploy/` was edited so it still loads.

## Follow-ups

| # | Follow-up | Code |
|---|---|---|
| 1 | `plan.expand`'s `step` template is evaluated at step start in the expand step's scope, so an expression meant for the created step can refuse the expand step; a template input kind left unevaluated would fix it | `RunCore.StartInputs`, `PlanExpansion.Build` |
| 2 | `POST /runs` could refuse every load-time diagnostic, not only check 18 (LOG 495) | `ApiApp.Runs.cs` |

The A6 follow-ups not in this batch (agent planning authority, the merge queue and the rest of `docs/build/a6-results.md`'s
table) and A4 follow-ups 1, 3 and 6 to 8 stand as listed there.
