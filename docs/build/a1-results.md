# A1 results: control gaps

Milestone A1 of `docs/12-build-plan-phase-2.md` section 4, built 2026-11-21 on `stuart-parkinson_xero/feature-flow-a1`
by three parallel builders (caps; the merge decision; load-time refusals), an independent review and a fix pass. Every
done-when item is built and shown by a test seen to fail when the code it guards is broken. No live run was needed.

## What was built

| Item | Change |
|---|---|
| F1, locked caps | One cap function, `StepCaps.Breaches` (`src/FeatureFlow.Validation/StepCaps.cs`), applied by check 22 at run start (`RunStartChecks.Caps`) and to every step of a patched plan (`Patches.CheckCeilings`). A locked `merge.require_checks` admits the input unset or a literal `true`; any other value, an expression included, is refused. The caps travel in `RunPolicy`, so they survive continue-as-new. `scm.merge` does not check them again (LOG 347) |
| F1, `merge.require_tested` | A lockable organisation policy key, applied by the same function to `scm.merge`'s `require_tested` input. The effective policy view shows it beside `require_checks` |
| F1, races | A `limits.races` asked for (by the step or its document's `defaults.limits`) above `policy.attempts.ceiling.per_step` is refused at run start and in a patch; a merge step that asks for none gets the installation's default held to the ceiling (LOG 340, 348) |
| F2, `<level>.merge_action` | `SourceControl.ActionRequestAsync` raises the decision on the running `scm.merge` step, on the existing `decision` wait kind (LOG 331, 350; `src/FeatureFlow.Interpreter/ActionDecisions.cs`). `approve` restarts the watch, which asks a person to merge; `reject` ends the step `declined` (LOG 349). A merge on the code host before the answer is accepted: the decision closes as answered on the code host, the timeline records the merger's login and that the decision was not given through Feature Flow, and no intervention is raised (LOG 341). Approval rules on `*.merge_action` decide who may answer; the decision opens whether or not a rule names it (LOG 351). The effective policy view shows who answers it |
| F3, small part | `notify.send` removed from the action contracts and `docs/04`; a plan naming it is refused at load as an unknown action. An action no provider or stand-in can serve returns `cannot_answer` from `RunActivities.RunActionAsync` and the run raises an intervention; before, the stand-in lookup threw and was retried for ever (LOG 352) |
| F19 | `providers.toolset`, a role's `toolsets` and a library item's `requires.toolsets` refused at load, naming the key; their model fields and std's role `toolsets` removed (LOG 354). The design stays in `docs/06` section 3.9 (LOG 339) |
| L4, part | The `scripted` agent runtime refused outside `development: true` |
| L9 | Check 12 refuses an `artifact.import` that declares or names an evidence type; a type set by an expression is refused at run time (LOG 353) |
| Documents | `docs/04` sections 1, 2, 4.5, 7.3, 10.1, 12; `docs/05` sections 2, 3.1, 4, 7; `docs/06` sections 3, 3.9, 3.10, 5; `docs/07` (refused keys; the example installation no longer configures a toolset, and its per-step ceiling is 12 so std loads); `docs/08`; `docs/09` checks 12 and 22; `docs/01`, `03` (no toolsets in version 1); `docs/02` section 9.3. No "planned (`docs/12` A1)" marker remains |
| Fixtures | Test libraries that used `notify.send` as a stand-in placeholder use `artifact.compose`, `repo.inspect` or a scripted agent step (LOG 355); eight replay histories re-recorded |

## Checks

| Done when | Test |
|---|---|
| A locked `require_checks` loosened by an expression, and by a patch, is refused; the same for `require_tested` | `RunStartCheckTests.Check_22_a_locked_merge_floor_refuses_any_value_but_a_literal_true` (4 cases with positives); `PatchTests.Check_22_a_patch_cannot_loosen_a_locked_merge_floor_by_a_literal_or_an_expression` (4 cases with positives) |
| `limits.races` above the per-step ceiling refused at run start, and by a patch | `RunStartCheckTests.Check_22_races_above_the_per_step_ceiling_are_refused_and_a_merge_asking_none_is_held_to_it` (step and document defaults); `PatchTests.Check_22_a_patch_cannot_set_races_above_the_per_step_ceiling_and_a_merge_asking_none_is_held_to_it` |
| A rule requiring approval on `ticket.merge_action` opens the decision; once answered, the person is asked to merge | Integration `MergeActionDecisionTests.The_merge_action_decision_once_approved_asks_a_person_to_merge_and_their_merge_completes_the_run`; `ActionDecisionTests` (5); `SourceControlActionTests.An_action_request_raises_its_merge_action_decision_and_asks_a_person_to_merge_only_once_it_is_approved`; `ApprovalTests.A_rule_on_merge_action_restricts_who_may_answer_the_merge_steps_decision` |
| A merge on the code host before the answer closes the decision, records the login, raises no intervention, and the run continues | Integration `MergeActionDecisionTests.A_merge_on_the_code_host_before_the_answer_closes_the_decision_records_who_merged_and_the_run_continues`; `ActionDecisionTests.A_merge_on_the_code_host_before_the_answer_…`; `SourceControlActionTests.A_merge_on_the_code_host_while_the_decision_is_open_…` |
| A plan using an unservable action stops at an intervention | `UnservableActionTests.An_action_no_provider_can_serve_cannot_answer`; `UnservableActionTests.A_plan_using_an_action_no_provider_can_serve_stops_at_an_open_intervention` (through `RunWorkflow.Ending`, LOG 356) |
| Each refused setting has a load test naming its key | `ConfigurationTests.An_installation_refuses_what_it_does_not_read` (`providers.toolset`, `scripted`); `ConfigurationTests.A_development_installation_may_use_the_scripted_runtime`; `OrganisationTests.A_librarys_toolset_settings_are_refused_naming_the_key`; `MutationTests` cases `removed_action` (`notify.send`), `import_produces_evidence`, `import_file_as_evidence`; `ArtifactActionTests.Import_refuses_at_run_time_a_file_whose_resolved_type_is_an_evidence_type` |

Suites at the final head: build with 0 warnings; the default suite, 24 projects, all passed; the 59 Integration
tests A1 touches (child runs, continue-as-new, delivery, steering, wait hand-over, merge decision, forge tickets,
epics, merge races, policy, findings loop, tracker update) passed on a local Temporal dev server; all 35 replay
histories replay.

## Controls

Each broke the guarded code, ran the test, saw it fail, and restored it. The builders and the reviewer recorded 30
such checks between them (their reports); the sub-orchestrator repeated a sample independently:

| Mutation | Test seen to fail |
|---|---|
| The bypass note writes "someone" instead of the merger's login (`SourceControl.cs`) | `SourceControlActionTests.A_merge_on_the_code_host_while_the_decision_is_open_…` |
| The `scripted` refusal never matches (`Configuration.cs`) | `ConfigurationTests.An_installation_refuses_what_it_does_not_read` (scripted case) |
| `StepCaps.RacesAsked` ignores the document's `defaults.limits` | No test failed: a gap. The races test gained a `defaults.limits` case, which fails with the line broken and passes restored |
| (reviewer) a locked floor admits an expression; the race ceiling raised by 100; the clamp removed; reject treated as approve; the old stand-in throw restored; a role's `toolsets` accepted; check 12 skips `artifact.import` | The matching tests above |

## Deviations and judgement calls

LOG 347 to 356. LOG 351 (the decision opens on every action request, not only where a rule names `*.merge_action`)
is flagged for the product owner: with `engine_can_merge: false` a run now waits for an approval before asking
anyone to merge, though a merge made directly on the code host is still accepted.

## Follow-ups

| # | Follow-up |
|---|---|
| 1 | LOG 351: the product owner to confirm that `merge_action` opens on every action request |
| 2 | LOG 349: whether a rejected merge should also close the pull request; today it is left open |
| 3 | No replay history covers the `merge_action` path; record one from `MergeActionDecisionTests` and add a row to the history path table when the histories are next re-recorded (A2 re-records them) |
| 4 | `FeatureFlow.Validation` cannot reference the interpreter, so `EffectivePolicy` writes `<level>.merge_action` literally, as `PlanValidator` does for the other engine-raised types; A2's action registry (F10) can give the type name one owner |
| 5 | A locked-false `require_checks` or `require_tested` has no effect: a floor bounds only from below. Refusing it at load as meaningless is new scope |
