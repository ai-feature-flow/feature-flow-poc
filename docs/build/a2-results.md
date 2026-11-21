# A2 results: engine simplification

Milestone A2 of `docs/12-build-plan-phase-2.md` section 4, built 2026-11-21 on `stuart-parkinson_xero/feature-flow-a2`
from a design note, five builder tasks (shared rules; action registry; stand-in removal; integration tests moved to the
memory providers, in three parallel parts; wait routing; shell to core), an independent review and two fix passes.
Every done-when item is built and shown by a test seen to fail when the code it guards is broken. No live run was
needed. Every replay history was re-recorded once (LOG 363).

## What was built

| Item | Change |
|---|---|
| F3, rest | The M3 action stand-in is deleted (`action_stand_in`, `IActionStandIn`, the scripted and recording stand-ins, `EngineActions.Built`, `HasSourceControl`, `RunPolicy.BuiltActions`, `WithoutSandboxAsync`); every action runs through its handler against the run's providers, and with none it cannot answer (LOG 367). A dry run selects each type's `dry_run` instance and nothing else, with no fallback to the default except the stores, secrets and credential broker; a mapped role gets the dry-run agent runtime (LOG 362, 384; `docs/07` section 5). The scripted and recording runtimes act in any sandbox through the sandbox provider (LOG 368). `wait.condition` on a tracker issue is built (LOG 369). The integration `Harness` runs on the memory providers; the k3d smoke runs one agent step against a trial Git server in the cluster (LOG 371) |
| F10, F14 code part | One `ActionDescriptor` per action in `ActionRegistry` (`src/FeatureFlow.Model/Actions.cs`) replaces the nine tables keyed by action name: outcomes, typed outputs, input schema, needs, handling, evidence type, hand-over interruption, raised gate type, race counting, and two facts that replace name tests in the core (LOG 357, 385). Handlers are bound by name in `EngineActions`; `EngineActions.Register(descriptor, handler)` adds an action as one entry. Every action has an input schema, `run.child` included, so `run_gate`, `level` and unknown keys are refused at load (check 31, LOG 358, 359). The `<level>.merge_action` type has one owner, the descriptor's `RaisesGate` (A1 follow-up 4). `ActionContract.Escalates` is deleted (L6) |
| F11, L2 | One routing function for every human wait: `RunCore.Route`, called by the `answer` update's validator and, through `RunCore.Answer`, by its handler (`src/FeatureFlow.Interpreter/Waits.cs`). `WaitKind.Question` added and agent questions held by the core, with timers armed by `ArmTimer` and fired back as `WaitTimerFired` (LOG 93 amended by LOG 364); `Decision` renamed `Gate` (LOG 360); closed waits in `RunState.Closed`; one recovery table, `Recoveries`; the console's recovery list read from the wait's `choices`. Overrides are routed the same way, by `RunCore.RouteOverride` and `RunCore.Override` (LOG 383) |
| F8 | Children, open and closed waits, consumed and produced artifacts, step and scope specs, session facts and trace notes moved from the Temporal shell (`RunWorkflow*.cs`) into the interpreter core (`Artifacts.cs`, `StepSpecs.cs`, `Children.cs`, `Sessions.cs`, `Waits.cs`); continue-as-new carries the core's snapshot plus what is Temporal's (`ShellState`), with SP1's drain counters on each `HandOver` (LOG 365). `ShellRuleTests` holds the shell to an allow-list of core members and fields (LOG 382) |
| L1 | One home each for step lookup (`Plan.FindStep`, `LongestStepPrefix`), duration parsing (`Durations.Parse`), short revisions and work references (`Revisions`, `WorkRefs`; the superseded reference uses 12 characters, LOG 366) (LOG 379) |
| L6 | `LooksLikeReference`, `Escalates`, the drain counters as state, `ShellState`'s compatibility defaults and stale milestone comments deleted; namespace `FeatureFlow.Engine.ActionSteps` renamed to match its folder |
| L7 | `tests/FeatureFlow.Testing.Support` (`Repository.Root`) replaces 24 copies of the root-finding helper |
| Documents | `docs/02` sections 2.1 and 3; `docs/04` sections 2, 3.4, 4, 5.1, 6.2, 8.2; `docs/05` section 5; `docs/06` (stand-in paragraph removed); `docs/07` section 5 and the provider kinds; `docs/08`; `docs/09` check 31 and section 4.2; `deploy/README.md`. LOG 93 amended; LOG 81, 121 and 317 superseded. No "planned (`docs/12` A2 …)" marker remains |

## Checks

| Done when | Test |
|---|---|
| The default and integration suites pass with the stand-in deleted | Default suite, 24 projects; Integration category, 120 tests; Cluster category on k3d, 7 + 16; replay, 42 histories. `ConfigurationTests.An_installation_refuses_what_it_does_not_read` (`action_stand_in` is an unknown type); `DryRunStartTests`; `TicketRunTests.A_dry_run_acts_on_its_dry_run_providers_and_records_each_agent_step` |
| The architecture test fails if the shell holds a routing decision the core can make | `ShellRuleTests.The_shell_uses_no_routing_vocabulary`, `The_shell_holds_no_routing_state`, `The_vocabulary_rule_sees_a_shell_that_decides_for_itself`. It failed on the shell before T5 (100 violations, then 54 under the allow-list) and on each break below |
| Adding a test action needs one registry entry and nothing else | `ActionRegistryTests.A_test_action_needs_one_registry_entry_and_nothing_else`: one `EngineActions.Register` call; the plan loads, check 31 refuses an unknown key at load, run start, the core, the shell's `StepSpec` mapping and dispatch all serve it. `Every_action_the_engine_carries_out_has_a_handler_and_no_handler_is_for_anything_else` |
| A plan with `run_gate` on `run.child` is refused at load | `MutationTests` cases `run_child_run_gate`, `run_child_level`, `unknown_input`, `input_literal_value` |
| Agent questions, gates and interventions open, answer and time out through one routing function, each with a re-drive test | `WaitRoutingTests` (14 tests, each answer checked through `Route` and `Answer` together); re-drive tests (workflow test files, `docs/09` section 4.2): `WorkflowTestRunnerTests.Each_wait_kind_is_answered_through_the_cores_routing` (gate, breakpoint, intervention), `A_patch_approval_is_answered_through_the_cores_routing`, `A_question_is_answered_through_the_cores_routing_times_out_or_closes_with_its_step`, std's `ticket_agent_questions`; `OverrideRoutingTests` (5); the `question_timeout` and `merge_action` replay histories. Only questions have a deadline: gates, breakpoints, interventions and patch approvals wait until answered (LOG 386) |

## Controls

Each broke the guarded code, ran the test, saw it fail, and restored it. The builders, fixers and reviewer recorded
about 60 such checks (their reports); a sample:

| Mutation | Test seen to fail |
|---|---|
| The shell skips `run.child` itself in `StartStepAsync`; adds a `List<OpenWait>` field; checks questions before `Route` in `ValidateAnswer` | `ShellRuleTests` (each) |
| (review) a wait kind decided from the `gate-` prefix; `run.child` skipped through a concatenated literal; a duplicate-answer ledger read from `core.State.Closed` | `ShellRuleTests`, after the allow-list (LOG 382); before it, all three passed |
| `StepSpec` refuses `test.*` actions | `A_test_action_needs_one_registry_entry_and_nothing_else`, after the fix; before it, the test passed |
| Dispatch hard-coded; a derived table reads `ActionRegistry.Default` | `A_test_action_needs_one_registry_entry_and_nothing_else` |
| Check 31 skips `run.child` | `MutationTests` `run_child_run_gate`, `run_child_level` |
| A question's step ending leaves it open; a timer fires before the deadline | `WaitRoutingTests` |
| A dry run falls back to default providers | `DryRunStartTests`, `UnservableActionTests`, `CompositionTests` |
| Two activity calls swapped in `StartChildAsync` | Replay, five histories (`TMPRL1100` nondeterminism) |

The sub-orchestrator repeated four independently on the merged branch, each seen to fail and then pass when restored:
a field added to `RunWorkflow` (`ShellRuleTests.The_shell_holds_no_routing_state`); check 31 at load skipping child
runs (`MutationTests` `run_child_run_gate`, `run_child_level`); a question timing out a minute before its deadline
(`WaitRoutingTests.A_question_times_out_at_its_deadline_and_then_refuses_an_answer`); a dry run falling back to default
providers (`DryRunStartTests`, two `UnservableActionTests` cases).

## Size

The workflow shell went from 2,252 to 1,526 lines (`RunWorkflow.cs` 1,452 to 1,054, `RunWorkflow.Sessions.cs` 438 to
259, `RunWorkflow.Overrides.cs` 316 to 170). `src` as a whole grew by about 900 lines (+2,989, −2,080). The growth is
the core taking over the shell's logic with tests in `Interpreter.Tests`, the input schemas for all twenty actions, the
tracker condition, the scripted runtime made sandbox-agnostic (moved from tests), and doc comments. Concepts removed:
the action stand-in and its provider type, the no-sandbox agent path, nine action tables, the shell's question store,
answer ledger and override ledger, `RunCore.Check`, `ToInput`, three recovery lists, `ActionContract` and its escalate
default, and 24 root-finding helpers.

## Deviations and judgement calls

LOG 357 to 387. Two are flagged for the product owner: LOG 362 (dry run works in a development installation, on memory
and recording providers, and is refused in production) and LOG 370 (notifications about a dry run go to the real
channels, as before).

## Follow-ups

| # | Follow-up |
|---|---|
| 1 | LOG 362: whether dry run must work in a production installation; that needs read-through recording providers for VCS, code host, CI and tracker |
| 2 | LOG 370: whether a dry run should notify real channels |
| 3 | The `fake` provider kind remains for the k3d smoke's code host (LOG 381); removing it needs a code host the cluster can serve |
| 4 | `ShellRuleTests` is a syntax check: a routing decision built from run-time values can still evade it (LOG 382). Review keeps it honest |
| 5 | The `merge_action` gate has no workflow-test form; `WaitRoutingTests` and its replay history cover it (LOG 386) |
| 6 | Action names remain as evidence producers (`ReadableForm.cs`, `StdValidators`) and in `artifact.import`'s refusal (LOG 385), as the design decided |
| 7 | `SteeringRunTests.An_override_arriving_while_the_run_hands_over_is_carried_into_the_next_run` failed under load because its forty chained steps, now scripted agent steps, outlasted the test's wait; the test now holds the drain open with one long parallel step and sends one override into it (14 of 14 loaded runs passed). `ContinueAsNewTests`' SP1 test sends answers on the same timing pattern and has not failed; under two concurrent full suites a few agent and steering tests timed out |
