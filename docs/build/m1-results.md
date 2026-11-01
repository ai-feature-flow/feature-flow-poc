# M1: plan loader and validator

Status: built 2026-11-02 on branch `m1-loader`. Every std template and fragment
loads with no diagnostic. Every blocking load check has a negative test that
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
exactly once, validates the copy as a library, and asserts that the set of
blocking check numbers is exactly the expected one
(`tests/FeatureFlow.Validation.Tests/MutationTests.cs`).

| Check | Mutation of std | Seen |
|---|---|---|
| 1 Schema | `implement` gains an unknown key `retries` | 1 only |
| 2 Unknown action or construct | `verify_and_review`'s `analysis` runs `analysis.execute`; an entry `{ id, sleep }` added to `ticket_delivery` | 2 only, both |
| 3 Duplicate id | `approve_spec` renamed `implement` | 3 only |
| 4 Undeclared parameter | `implement` binds `params.ticket` | 4 only |
| 5 Expression | A merge-bar guard `findings.open_blocking ==`; `fix_loop`'s triager output `to_fix` renamed, so a guard reads an undeclared output | 5 only, both |
| 6 Outcomes | `implement` declares `attempts_exhausted`; the partial-delivery decision declares none | 6 only, both |
| 7 Every outcome routed | The partial-delivery decision's classless `retry` loses its route; `implement`'s `blocked` loses its class | 7 only, both |
| 8 Route target | `approve_spec` rejects to `plan_spec`; `open_pr`'s guarded route loses its fallback | 8 only, both |
| 9 Reachable | A `notify.send` step after `deliver`, whose exits all route elsewhere | 9 only |
| 10 Cycle with a route out | `fix_loop`'s `tests_added` sends `valid` back to `fix` | 10 only |
| 11 Consumed artifact producer | `implement` consumes `delivery_plans`; the fixer produces `implementation_claims` as `triage` | 11 only, both |
| 12 Evidence type | `implement` also produces a `test_results` artifact | 12 only |
| 13 Write access and tools | The verifying reviewer gets `write_access: branch`; `implement` sets `tools` | 13 only, both |
| 14 Fragment | `planner_task` unbound; `reverify` unrouted; `standard_review` uses `trace`, which uses `baseline`, four deep | 14 only, all three |
| 15 Parallel branch routes | The revert branch step gains `routes` | 15 only |
| 16 Decision type | `ticket.spec_approvel` | 16 only |
| 17 Plan size | The ticket's review given 1,000 angles: 2,024 steps | 17 only |
| 19 Reference resolves | `std:roles/implementor` | 19 only |
| 34 Unused parameter | A parameter `notify_channel` added and not used | Warning 34 only, no error |
| 35 Deprecated reference | `std:roles/fixer` marked deprecated with a replacement | Warning 35 only, no error |

**Every check was seen to fail when broken.** For each of checks 1 to 17, the
check's diagnostics were turned into warnings and the mutation tests run
again. Each time, exactly that check's mutation tests failed and the others
passed (script and output kept outside the repository with the coordinating
session's notes):

| Check made a warning | Mutation tests failing |
|---|---|
| 1 | `unknown_key` |
| 2 | `unknown_action`, `unknown_construct` |
| 3 | `duplicate_id` |
| 4 | `undeclared_parameter` |
| 5 | `expression_parse`, `expression_unknown_output` |
| 6 | `declared_exhausted`, `no_outcomes` |
| 7 | `unclassed_outcome`, `unrouted_outcome` |
| 8 | `guard_without_fallback`, `missing_target` |
| 9 | `unreachable_step` |
| 10 | `cycle_without_exit` |
| 11 | `incompatible_producers`, `missing_producer` |
| 12 | `agent_produces_evidence` |
| 13 | `reviewer_write_access`, `step_sets_tools` |
| 14 | `fragment_exit_unrouted`, `fragment_parameter_unbound`, `fragment_too_deep` |
| 15 | `branch_routes` |
| 16 | `undeclared_decision_type` |
| 17 | `plan_too_large` |

Other checks seen to fail when broken: std itself is the positive case for
every mutation (`StdLibraryTests`); a golden file edited by one character
makes its readable-form test fail; an edit to `ticket_delivery` changes its
readable form (`A_change_to_a_template_changes_its_readable_form`).

No check lacks a mutation of std.

The default category has 140 tests, up from M0's 21: `Validation.Tests` 50
(27 mutations, 2 warning mutations, std loading, golden files),
`Expressions.Tests` 45, `Architecture.Tests` 17, `Cli.Tests` 12,
`Model.Tests` 11, `Library.Tests` 5.

## 3. Errors found in std

None. Every template and fragment loaded clean the first time the validator
ran with every check in place. Two things the validator first reported were
validator bugs, not std errors: route guards on a fragment `use` entry were
not collected (so `check_satisfaction` looked unused), and fragments validated
on their own did not know their caller's defaults. Std's README status line is
updated to say it is now validated.

## 4. Design changes

LOG 71 to 74, unreviewed:

| LOG | Change |
|---|---|
| 71 | CEL macros are off, so `all`, `any` and `exists` are unavailable: Cel.NET costs a comprehension over a list of unknown length at Cost.Max 9223372036854775807, which the cap refuses. The cap is Spike 1's Cost.Max 100. `docs/03` section 5.2 carries a pointer |
| 72 | A fragment's steps take route defaults from the fragment, then from each caller up to the template. Std depends on it. `docs/03` section 11 carries the rule |
| 73 | Deprecation format: `metadata.deprecated: { replacement, reason }` on YAML items, for check 35 |
| 74 | For check 10, escalations and the `attempts_exhausted` default are not a route out of a cycle |

None overturns an approved decision.

## 5. Implementation choices

None changes the design, so none has a LOG entry.

| Choice | Reason |
|---|---|
| Documents are read into a YAML node tree, then bound to the model by hand-written readers, not by YamlDotNet's deserialiser | A fragment's `{ param: }` may stand in any field, including ones typed as strings or numbers (LOG 62), so substitution has to happen before binding. The readers give each unknown key its line and column |
| Classification of cases the check list does not name: an outcome name in `routes`, `produces`, `verdicts`, `ratifies`, `auto` or `tracker.on` that the step does not have is check 1; a `use` route or binding naming something the fragment does not declare is check 14; `{ param: x }` naming an undeclared parameter is check 4; an artifact type a document does not list in `artifact_types` is check 11; a tracker status not in the status map is check 1; an entry with no step form is check 2 | Each needs a number, and each sits nearest the check named |
| A reference that does not resolve at load is reported as check 19 | Check 19 is a run-start check, but loading needs the item (a fragment, a role's write access, an artifact type's evidence flag) and must say why it cannot |
| A `for_each` whose list is known only at binding (`run.repositories`) is expanded with one representative branch; a list reached through literal defaults (std's review angles) is expanded in full | Loading a template has no run. The readable form says which |
| A fragment validated on its own substitutes literal defaults, leaves other `{ param: }` fields unbound, treats a classed outcome without a route as left to its caller, and skips the consumer check (11) | It has no caller. Every std fragment is also checked in full inside a template |
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
| 1 | LOG 71 to 74 need review. LOG 71 is the one that removes something the design promised |
| 2 | Should validation compute the effective policy view (`docs/09` section 2.3) now, or at run start in M3 as the check list places it? M1 shows only each merge step's `engine_can_merge` |
