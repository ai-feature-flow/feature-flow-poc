# M1: plan loader and validator

Status: built 2026-11-02 on branch `m1-loader`, and revised the same day after
review (section 8). Every std template and fragment loads with no diagnostic. Every blocking load check has a negative test that
mutates std and triggers that check and no other. The readable forms of the
three std templates are golden files. The CI workflow's commands pass from a
clean clone (section 6). GitHub CI has not run, for the reason in
`docs/build/m0-results.md` section 5.

## 1. What was built

| Project | Holds |
|---|---|
| `Model` | A YAML node tree with positions (YamlDotNet's representation model); `MapReader`, which refuses unknown keys as check 1; library references; the action catalogue of `docs/04` section 2; readers for the manifest, roles, artifact types, decision types, the status map, template and fragment headers, and steps; the expanded plan's records |
| `Expressions` | CEL restricted as `docs/03` section 5.2 says, on an explicit allow-list (`CelExpressions`); `{{ }}` extraction from task and prompt text |
| `Library` | The `directory` source (`Library`), a set of libraries that resolves references (`LibrarySet`), and `PlanLoader`: fragment expansion with `{ param: }` substitution and ids prefixed `<use id>.` (LOG 62), `for_each` expansion into a `parallel` or a chain with ids `<id>_<index>` (`docs/03` section 8.2), and route resolution with defaults (section 4.3) |
| `Validation` | `PlanValidator` (the load checks), `LibraryValidator` (a whole library, or one item), `ReadableForm` |
| `Cli` | `ff validate <library folder or template/fragment file> [--library <folder>]...` and `ff show <template file>` |

`ff validate library/std` prints `0 error(s), 0 warning(s)`. A library is
validated by loading every template in full, every fragment on its own, and
every role, artifact type and decision type.

Spike 1's four CEL checks are ported as `Expressions` tests (`docs/11`
section 6): restriction (28 cases), determinism (6 expressions, 4,000
evaluations each, both map orders), the static cost estimate, and the length,
node and cost limits.

## 2. Checks

Each negative test copies `library/std`, applies a text edit that must match
exactly once, validates the copy as a library, and asserts two things: the set
of blocking check numbers is exactly the expected one, and every error contains
the text of the one rule the mutation targets
(`tests/FeatureFlow.Validation.Tests/MutationTests.cs`). The second assertion
means that deleting any one rule of a check fails a test, even where a check
has several rules.

| Check | Mutation of std | Seen |
|---|---|---|
| 1 Schema | `implement` gains an unknown key `retries` | 1 only |
| 2 Unknown action or construct | `verify_and_review`'s `analysis` runs `analysis.execute`; an entry `{ id, sleep }` added to `ticket_delivery` | 2 only, both |
| 3 Duplicate id | `approve_spec` renamed `implement` | 3 only |
| 4 Undeclared parameter | `implement` binds `params.ticket` | 4 only |
| 5 Expression | A merge-bar guard `findings.open_blocking ==`; `fix_loop`'s triager output `to_fix` renamed, so a guard reads an undeclared output; a guard `steps.triage.outputs.to_fix`, a number; a guard `params['nope'] == 1 && steps['triage'].outputs.bogus > 0` | 5 only, all four |
| 6 Outcomes | `implement` declares `attempts_exhausted`; the partial-delivery decision declares none | 6 only, both |
| 7 Every outcome routed | The partial-delivery decision's classless `retry` loses its route; `implement`'s `blocked` loses its class; `fix_loop` loses its `defaults`, so its blocked agents have no route (LOG 72) | 7 only, all three |
| 8 Route target | `approve_spec` rejects to `plan_spec`; `open_pr`'s guarded route loses its fallback | 8 only, both |
| 9 Reachable | A `notify.send` step after `deliver`, whose exits all route elsewhere | 9 only |
| 10 Cycle with a route out | `fix_loop`'s `tests_added` sends `valid` back to `fix` | 10 only |
| 11 Consumed artifact producer | `implement` consumes `delivery_plans`; the fixer produces `implementation_claims` as `triage` | 11 only, both |
| 12 Evidence type | `implement` also produces a `test_results` artifact | 12 only |
| 13 Write access and tools | The verifying reviewer gets `write_access: branch`; `implement` sets `tools` | 13 only, both |
| 14 Fragment | Required `subject_id` unbound; `planner_task` made optional with no default and left unbound, so its `{ param: }` has no value; `planner_task` bound to an expression; `reverify` unrouted; `standard_review` uses `trace`, which uses `baseline`, four deep | 14 only, all five |
| 15 Parallel branch routes | The revert branch step gains `routes` | 15 only |
| 16 Decision type | `ticket.spec_approvel` | 16 only |
| 17 Plan size | The ticket's review given 1,000 angles: 2,024 steps | 17 only |
| 19 Reference resolves | `std:roles/implementor` | 19 only |
| 34 Unused parameter | A parameter `notify_channel` added and not used | Warning 34 only, no error |
| 35 Deprecated reference | `std:roles/fixer` marked deprecated with a replacement | Warning 35 only, no error |

**Every check was seen to fail when broken.** `tools/check_controls.py` turns
each of checks 1 to 17 in turn into a warning, runs the mutation tests, and
restores the source. Each time, exactly that check's mutation tests failed and
the others passed. The script exits 0 only when that holds for every check;
it did, on the revised code:

| Check made a warning | Mutation tests failing |
|---|---|
| 1 | `unknown_key` |
| 2 | `unknown_action`, `unknown_construct` |
| 3 | `duplicate_id` |
| 4 | `undeclared_parameter` |
| 5 | `expression_parse`, `expression_unknown_output`, `guard_not_bool`, `index_access` |
| 6 | `declared_exhausted`, `no_outcomes` |
| 7 | `fragment_without_defaults`, `unclassed_outcome`, `unrouted_outcome` |
| 8 | `guard_without_fallback`, `missing_target` |
| 9 | `unreachable_step` |
| 10 | `cycle_without_exit` |
| 11 | `incompatible_producers`, `missing_producer` |
| 12 | `agent_produces_evidence` |
| 13 | `reviewer_write_access`, `step_sets_tools` |
| 14 | `fragment_exit_unrouted`, `fragment_param_needs_literal`, `fragment_param_no_value`, `fragment_required_unbound`, `fragment_too_deep` |
| 15 | `branch_routes` |
| 16 | `undeclared_decision_type` |
| 17 | `plan_too_large` |

Other checks seen to fail when broken: std itself is the positive case for
every mutation (`StdLibraryTests`); a golden file edited by one character
makes its readable-form test fail; an edit to `ticket_delivery` changes its
readable form (`A_change_to_a_template_changes_its_readable_form`). The tests
added in review were run against the reviewed code (`3f7b2f9`) and failed
there: the two branch tests, `guard_not_bool` and `index_access`; and
`fragment_without_defaults` gives no error there.

No check lacks a mutation of std.

The default category has 150 tests, up from M0's 21: `Validation.Tests` 57
(32 mutations, 2 warning mutations, std loading and routing, golden files),
`Expressions.Tests` 48, `Architecture.Tests` 17, `Cli.Tests` 12,
`Model.Tests` 11, `Library.Tests` 5.

## 3. Errors found in std

None under the rules as first built. Under LOG 72 as revised in review, five
std fragments had agent steps whose `blocked` outcome had no route, because a
fragment no longer takes its caller's defaults. Each is fixed in std:

| Fragment | Fix |
|---|---|
| `fix_loop` | `defaults: { routes: { by_class: { blocked: { exit: escalated } } } }`: a blocked triager or fixer leaves by the `escalated` exit, which `verify_and_review` routes |
| `verify_and_review` | The same default: a blocked repair agent leaves by the `escalated` exit, which the template routes |
| `merge_into_parent` | `resolve_conflict` routes `blocked: { escalate: true }`; the fragment has no escalation exit |
| `plan_and_validate` | `plan` routes `blocked: { escalate: true }` |
| `plan_children` | `plan` routes `blocked: { escalate: true }` |

The last three keep the behaviour std had through the template's default.
Std's README status line is updated to say it is now validated.

## 4. Design changes

LOG 71 to 74, unreviewed:

| LOG | Change |
|---|---|
| 71 | **Overturns part of an approved section and awaits a decision.** `docs/03` section 5.2 allows `all`, `any` and `exists`; the code keeps CEL macros off, so they are unavailable. Cel.NET costs a comprehension over a list of unknown length at Cost.Max 9223372036854775807, which the cap (Spike 1's Cost.Max 100) refuses. Std uses none of them. `docs/03` is left as approved. Untried alternative: a cost estimate that assumes the 16-entry bound on an output (section 5.4) |
| 72 | A fragment's steps take route defaults only from the fragment's own `defaults` and the engine's built-in defaults, never the caller's; a branch step's unrouted outcome goes to the join. `docs/03` section 11 states the rule as pending review. Std's fixes are in section 3 |
| 73 | Deprecation format: `metadata.deprecated: { replacement, reason }` on YAML items, for check 35 |
| 74 | For check 10, escalations and the `attempts_exhausted` default are not a route out of a cycle |

LOG 71 overturns part of an approved section and awaits the product owner's
decision. LOG 72 to 74 fill gaps; none overturns an approved decision.

## 5. Implementation choices

None changes the design, so none has a LOG entry.

| Choice | Reason |
|---|---|
| Documents are read into a YAML node tree, then bound to the model by hand-written readers, not by YamlDotNet's deserialiser | A fragment's `{ param: }` may stand in any field, including ones typed as strings or numbers (LOG 62), so substitution has to happen before binding. The readers give each unknown key its line and column |
| Classification of cases the check list does not name: an outcome name in `routes`, `produces`, `verdicts`, `ratifies`, `auto` or `tracker.on` that the step does not have is check 1; a `use` route or binding naming something the fragment does not declare is check 14; `{ param: x }` naming an undeclared parameter is check 4; an artifact type a document does not list in `artifact_types` is check 11; a tracker status not in the status map is check 1; an entry with no step form is check 2 | Each needs a number, and each sits nearest the check named |
| A reference that does not resolve at load is reported as check 19 | Check 19 is a run-start check, but loading needs the item (a fragment, a role's write access, an artifact type's evidence flag) and must say why it cannot |
| A `for_each` whose list is known only at binding (`run.repositories`) is expanded with one representative branch; a list reached through literal defaults (std's review angles) is expanded in full | Loading a template has no run. The readable form says which |
| A fragment validated on its own substitutes literal defaults, leaves other `{ param: }` fields unbound, leaves its exits unrouted, and skips the consumer check (11) | It has no caller. Every std fragment is also checked in full inside a template |
| A step output's declared type reaches the type checker: `steps.<id>.outputs.<name>` is declared as a qualified name with its type, as are `outcome` (string) and `executions`. An output of type `number` is checked as a CEL int | Without it every step value is dyn and a guard of any type passes. CEL cannot declare equality between int and double, so `outputs.to_fix == 0` would not check if `number` were a double; comparisons across the two are declared |
| Index access on `params`, `steps`, `run` or `findings`, or on a step or its `outputs` or `artifacts`, is refused (check 5). Indexing a value, such as `run.branch['acme/api']` or a list output, is allowed | Index access would read a parameter or output by a name checks 4 and 5 never see |
| A step whose outcomes are unknown (unknown action, or none declared) keeps its written routes for the graph checks | Otherwise checks 2 and 6 cascade into 9 |
| Check 17 counts steps: more than 2,000 is an error | `docs/09` section 2.1: about 500 bytes per step, 1 MB of carried state |
| The readable form for a template: the header names the template and level and each merge step's `engine_can_merge`, traced through fragment bindings; status is `pending`; the foot adds the fragment uses and each `for_each`'s item source | `docs/09` section 4.4 shows a running plan. The foot keeps "one line per step" while showing the grouping `docs/03` section 11 asks for |
| `ff validate <file>` finds the library as the folder above `templates/` or `fragments/` | References name items, not files; a file path is a convenience for the command line |

## 6. CI

GitHub CI has not run on this work, for the reason in
`docs/build/m0-results.md` section 5. The workflow's commands were run from a
clean clone of `m1-loader` at `8e52c4a` on macOS, 2026-11-02. The clone was
made inside the working tree, where the session could run git, and deleted
afterwards:

| Job | Commands | Result |
|---|---|---|
| Build and default tests | `dotnet build FeatureFlow.slnx -c Release`; `dotnet test ... --filter "Category!=Integration&Category!=Live"` | 0 warnings, 0 errors; 140 passed, 0 failed |
| Integration tests | `temporal server start-dev --headless --port 7833`, with `FF_TEMPORAL_ADDRESS=localhost:7833`; `docker version` (29.9.2); the same build; `dotnet test ... --filter "Category=Integration"` | Server healthy; 0 warnings, 0 errors; 2 passed, 0 failed. Server stopped afterwards |

## 7. Open questions

| # | Question |
|---|---|
| 1 | LOG 71 overturns part of `docs/03` section 5.2: keep `all`, `any` and `exists` out, or bound their cost by the 16-entry output limit? LOG 72 to 74 need review |
| 2 | Should validation compute the effective policy view (`docs/09` section 2.3) now, or at run start in M3 as the check list places it? M1 shows only each merge step's `engine_can_merge` |

## 8. Review changes

Requested by the coordinator's review of `3f7b2f9`, 2026-11-02:

| # | Change |
|---|---|
| 1 | A branch step's unrouted outcome is reported to the join; document defaults no longer apply in a branch (`docs/03` section 8.1). Tests: `A_branch_with_no_defaults_reports_every_outcome_to_the_join`, and std's review branches in `Route_defaults_follow_list_order_and_classes` |
| 2 | Check 5 types step outputs from their declarations and refuses index access on namespaces and steps. Mutations `guard_not_bool` and `index_access` |
| 3 | The `docs/03` section 5.2 edit is reverted. LOG 71 says it overturns part of an approved section and awaits a decision, and names the untried alternative |
| 4 | LOG 72 rewritten: no fall-through to the caller's defaults. Std fixed (section 3). Test `A_fragment_step_leaves_by_its_exit_not_by_the_callers_defaults` and mutation `fragment_without_defaults` |
| 5 | Each check 14 mutation targets one rule, and every mutation asserts its rule's message. The control script is `tools/check_controls.py` |
| 6 | A comment's LOG reference corrected; a null-forgiving cast removed; check 34 reuses the parameter uses check 4 records instead of type-checking every expression again |

## 9. Follow-ups

Not fixed in M1:

| # | Follow-up |
|---|---|
| 1 | Check 17 counts steps, not carried state, so a `for_each` over `run.repositories` counts as one branch at load |
| 2 | References to a library missing from the manifest's `dependencies` are not checked |
| 3 | Check 35 covers only roles, artifact types and fragments |
| 4 | The effective policy view (`docs/09` section 2.3), open question 2 |
