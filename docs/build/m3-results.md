# M3: a runnable engine with fake providers

Status: built 2026-11-02 on branch `m3-engine` (pull request 1), based on `main` at `11e6aff`,
and revised the same day after review (section 9).
(LOG 75 to 80 approved). A std `ticket_delivery` run completes on a Temporal
dev server with fake providers, driven through the CLI. A std `epic_delivery`
run with six child tickets completes across several continues-as-new. SP1's
scenario, rewritten against this interpreter, loses no child's end and no
answer, and both of SP1's negative controls make it fail. Four recorded
histories replay in the default test category; removing the version guard of
a changed path makes three of them fail. GitHub CI has not run, for the reason
in `docs/build/m0-results.md` section 5.

## 1. What was built

| Project | Holds |
|---|---|
| `Interpreter` | Carried state (`Carry.cs`): the core's state as plain data, and `RunCore.Resume`, which loads the template with the carried patches and restores the state. Answers to interventions, abort, a step that cannot answer, a child's patch to its parent, a step's evaluated inputs, a child's outcome from its status (`Recovery.cs`). `Flush`, which takes commands without entering steps, for the drain. The records the API and the workflow exchange (`Wire.cs`, LOG 82) |
| `Engine` | `RunWorkflow`, the one workflow type, a thin loop around the core; `RunActivities`: run start, loading the lock, the agent step, the action stand-in, `plan.expand`'s read, the confirm read of a child, record writes |
| `Providers.Abstractions` | `IProvider`, `ProviderResult`, error classes (`docs/06` section 1.1), typed capability records and their names, the store interfaces and record types, `IAgentRuntimeProvider` without a sandbox (M4 adds it), `IActionStandIn` (LOG 81), `ProviderSet` |
| `Providers.Local` | Filesystem artifact store (content-addressed by SHA-256), SQLite record store, file secrets; the scripted fake runtime and action stand-in, driven by a fake script; the recording runtime and stand-in a `dry_run` selects; a described-only fake for any other type; the factories a host registers |
| `Hosting` | Reads the installation and organisation files; composes providers from factories by type and kind; runs the orchestration worker, the agent worker and the API in one process or separately |
| `Api` | Start, list, show, answer and abort, behind a local token. Queries read the record store. An answer is authorised, recorded with the authenticated actor, then delivered as an Update |
| `Cli` | `ff run start`, `show`, `wait`, `answer`, `recover`, `abort` over the API |
| `Model` | The installation and organisation readers (the keys M3 reads; others refused, LOG 88), a writer for node trees and plain values, a patch writer |
| `Library` | A library built from a snapshot of its files (`LibraryFiles`), and the lock (`LibraryLock`) |
| `Validation` | Run-start checks 19, 20, 21 and 22 (`RunStartChecks`) |
| `hosts/*` | The three hosts, each composing the local providers through `Hosting` |

The workflow:

- Its carried state is set in the `[WorkflowInit]` constructor (DR14). Before
  the core is built, a signal is buffered and an update handler waits.
- It continues-as-new by `docs/02` section 3. Past the threshold it starts
  nothing new and takes the core's commands with `Flush`. It waits for every
  in-flight activity, then for `Workflow.AllHandlersFinished` (DR15). It
  carries out and records whatever a handler produced, and takes the snapshot
  with no await after the last check.
- Children are started with `ParentClosePolicy.Abandon` and signal the
  parent's workflow id, with no run id. A signal is a hint: the parent reads
  the child's status and result in an activity before it completes the
  `run.child` step. Unconfirmed child ends are carried.
- Decisions, patch approvals and recovery actions are one Update, `answer`.
  The validator rejects only unknown and conflicting answers (LOG 58). Abort is
  an Update too.

## 2. Checks

### 2.1 Done when

| Test (`tests/FeatureFlow.Integration.Tests`) | Shows |
|---|---|
| `A_std_ticket_runs_to_completion_driven_through_the_cli` | `ff run start std:templates/ticket_delivery`, `ff run wait --for wait`, `ff run show`, `ff run answer` on the spec approval, `ff run wait`: the run completes. Its timeline holds the main path in order, from `baseline.tests` to `deliver.merge`. A repeated answer returns `duplicate`; a different answer is refused. No workflow task failed |
| `An_epic_with_children_runs_across_several_continues_as_new` | `epic_delivery` with a scripted work list of six tickets: `plan.expand` adds six `run.child` branches, each child a `ticket_delivery` run. The epic completes, each child completed once, with at least 3 hand-overs, at least one while children ran, no activity in flight at any hand-over, and one `continued_as_new` timeline entry per hand-over |
| `An_exhausted_step_escalates_and_granting_attempts_through_the_cli_completes_the_run` | `implement` fails three times and escalates. `ff run recover ... end completed` is refused; `ff run recover ... grant_attempts 1` runs it a fourth time and the run completes |
| `Abort_through_the_cli_ends_the_run_aborted` | The run ends `aborted` with no open wait |
| `A_dry_run_records_each_call_instead_of_acting` | The recording providers write every agent and action call to a file |
| `A_non_retryable_activity_failure_fails_the_attempt_and_escalates` | `plan.expand` reads a work list with no items. Each execution fails as a work error; after 3 the step escalates to an intervention instead of staying in flight |
| `A_run_needing_a_capability_no_provider_offers_is_refused_at_start` | With no `code_host` provider, the run is refused with check 21, and the record names the missing provider |

### 2.2 SP1's scenario

`ContinueAsNewTests` rewrites `spikes/04-continue-as-new` against this
interpreter. The parent template (`tests/FeatureFlow.Integration.Tests/libraries/sp1`)
is one `parallel` of three `for_each` branches: 24 child runs (at most 10 at
once, each an agent step the fake runtime holds for 5 to 40 seconds, a quarter
ending `failed`), 10 decisions open from the start, and a chain of 12 actions of
3 to 6 seconds. The run hands over every 100 history events, the floor (LOG 87). Even-numbered
decisions are answered through the API after a delay of up to 45 seconds;
odd-numbered ones by an Update sent as soon as the run reports it is draining.
Three answers are repeated and two contradicted. Seed 1.

| Check | Result |
|---|---|
| C1 Every child's end recorded once, with the outcome Temporal reports for the child when read directly | Pass |
| C2 Every answer recorded once, as sent; the two contradicting answers refused | Pass |
| C3 No activity started while draining; none in flight at any hand-over | Pass |
| C4 The run ends `partial` (some children failed) within 3 minutes | Pass, in about 80 seconds |
| C5 No `WorkflowTaskFailed` event in any run | Pass |
| E1 At least 3 hand-overs with children running and decisions open | 4 of 6 hand-overs |
| E2 At least one child's end arrived while draining | 3 to 4 |
| E3 At least one answer sent and received while draining | 5 to 6 |

The exercise figures are from runs of the revised code (section 9). SP1's threshold for
E2 was 3 across three runs; this test asks for 1 in its one run.

### 2.3 Replay

`tests/histories/` holds five histories, recorded with `FF_RECORD_HISTORIES`.
Four are the first runs of integration runs: `ticket_delivery`,
`epic_delivery`, `epic_delivery_child` and `sp1_parent`; the last three end in
a continue-as-new. The fifth, `sp1_parent_resumed`, is a later run of the SP1
scenario that resumed from a hand-over carrying children and child ends not
yet confirmed, recorded on the revised code; the test refuses to pass unless
such a run exists. `FeatureFlow.Replay.Tests` replays each with Temporal's
`WorkflowReplayer` in the default category, under the workflow id it ran with,
and checks that all five files are there.

After the first four were recorded, two behaviour changes were made, each
behind a patch marker:

| Change | Marker | With the guard replaced by `true` |
|---|---|---|
| At each hand-over the run writes a `continued_as_new` timeline entry, one more activity | `m3-record-hand-over` | `epic_delivery`, `epic_delivery_child` and `sp1_parent` fail with a nondeterminism error |
| The hand-over threshold is at least 100 events, and a run also hands over at 8 MB of history (section 9) | `m3-hand-over-limits` | `epic_delivery` and `epic_delivery_child` (recorded at a threshold of 60) and `sp1_parent_resumed` (which holds the marker) fail |

With both guards, all five replay. `ticket_delivery`, which never hands over,
passes either way.

Recorded histories contain no API token (the recording asserts it) and no
local path. They contain the library's text: LOG 83.

### 2.4 Controls

`tools/check_m3_controls.py` breaks each rule in the source, runs the tests
that guard it, restores the source and runs them again. It exits 0 only when
each control's named tests fail while broken and pass after the revert.

It held for all thirteen controls, run against a dev server on the revised
code of this branch (section 9):

| Control | Tests failing while broken | After revert |
|---|---|---|
| N1 (SP1): a child signals its parent's run id (`Workflow.Info.Parent.RunId`), not its workflow id | The SP1 scenario: the parent never confirms the children that end after the first hand-over and misses its 3-minute deadline | Passes |
| N2 (SP1): children take the default parent-close policy, terminate | The SP1 scenario: the children are terminated at the first hand-over and never report | Passes |
| No drain: the run continues-as-new with activities in flight | The SP1 scenario | Passes |
| Child ends that arrive while draining are not carried | The SP1 scenario: those children are never confirmed | Passes |
| The version guard of the hand-over record replaced by `true` | Replay of `epic_delivery`, `epic_delivery_child` and `sp1_parent` (nondeterminism) | Passes |
| The version guard of the hand-over threshold's floor and byte limit replaced by `true` | Replay of `epic_delivery`, `epic_delivery_child` and `sp1_parent_resumed` | Passes |
| A non-retryable step activity failure is not caught | `A_non_retryable_activity_failure_fails_the_attempt_and_escalates`: `tickets.expand` stays in flight and never escalates | Passes |
| Attempt grants not held to the hard ceiling | `A_grant_above_the_hard_ceiling_needs_an_administrator` (a steerer's grant of 2) and `A_grant_that_raises_the_run_ceiling_above_its_hard_ceiling_needs_an_administrator` | Passes |
| The library's `default_approval` ignored | `Who_may_answer_is_the_organisation_rule_then_the_library_default_then_the_fallback` | Passes |
| A repeated answer is not recognised as a duplicate (validator and handler) | The ticket test: the repeat is refused, not `duplicate` | Passes |
| Carried state drops the applied patches | `A_run_carried_after_every_input_takes_the_same_path_as_one_that_is_not` | Passes |
| Check 21 removed | `Check_21_names_each_capability_no_configured_provider_offers`; the integration test of a refused run | Passes |
| `Api` given a reference to `Engine` | `Every_project_under_src_and_hosts_obeys_the_reference_rules` | Passes |

The first full run of the script found that the duplicate-answer control did
not hold: the ticket test failed after the revert too. With instant fakes the
run completed before the repeated answer arrived, and the API returned 500 for
an update to a closed workflow. The test now holds its run open with a
3-second implementer, and the API answers 409 for a run that has ended. The
control was run again and held. In the revised run, the first edits of two new
controls (an always-false filter, an `if (false)`) did not compile, as warnings
are errors; the edits were changed and both controls run again and held.

### 2.5 Counts

The default category has 288 tests, up from M2's 226: `Validation.Tests` 73,
`Interpreter.Tests` 58, `Expressions.Tests` 48, `Testing.Workflows.Tests` 26,
`Model.Tests` 21, `Architecture.Tests` 20, `Cli.Tests` 15,
`Providers.Local.Tests` 7, `Api.Tests` 6, `Replay.Tests` 6, `Library.Tests` 5,
`Hosting.Tests` 3. The integration category has 10, up from 2: the two
environment tests, five ticket tests, the epic test, the step-failure test and
the SP1 scenario. A full integration run takes about 80 seconds.

## 3. Errors found in std

| File | Error | Fix |
|---|---|---|
| `library/std/library.yaml` | `requires.engine: ">=1.0.0"` refuses every run on this engine, version 0.1.0, until version 1 ships | `">=0.1.0 <2.0.0"` (LOG 90) |

Std's templates ran on the engine as M2's re-drive tests predicted: the
ticket's and the epic's main paths complete with the fake providers' first
success outcomes.

## 4. Design changes

LOG 81 to 90. LOG 83 is approved; the others are unreviewed:

| LOG | Change |
|---|---|
| 81 | A temporary provider type, `action_stand_in`, runs the 19 actions later milestones build; `fake` providers describe themselves so check 21 can pass. Either is refused unless the installation sets `development: true` |
| 82 | The workflow's names and argument records live in `FeatureFlow.Interpreter.Wire`, so `Api` and `Engine` need not reference each other |
| 83 | Workflow history holds the run's library text, returned by the activity that loads the lock. Approved by the product owner, 2026-11-02: accepted, and `docs/09` section 4.1 and `docs/02` section 5 amended to say so |
| 84 | Run start is split: the API writes the lock; the workflow's first activity runs checks 18, 19, 21 and 22; a refused run is recorded `failed`. Check 20 is built and tested but not called before M5 |
| 85 | Recovery details: grant attempts also raises the run ceiling, and beyond the hard ceiling needs an administrator; a person may not end a run `completed`; repeated answers are `duplicate`, different ones a conflict |
| 86 | A child's patch to its parent: authority checked in the child, `resume_at` optional, rejection recorded |
| 87 | The hand-over threshold is `temporal.continue_as_new_events`, at least 100; an administrator may set it per run; a run also hands over at 8 MB of history |
| 88 | Check 22's run-start scope; organisation keys M3 does not enforce are refused |
| 89 | A refused report fails the attempt until M4; who may answer: the organisation rule, then the library's `default_approval` roles, then a fallback; an administrator may do what a steerer may |
| 90 | The engine's version is 0.1.0 |

Each was checked against `docs/01` to `docs/11` and LOG 1 to 80. LOG 83
conflicted with `docs/09` section 4.1; the product owner accepted it and the
section was amended. The others fill gaps and contradict no approved rule.

## 5. Implementation choices

None changes the design beyond the LOG entries above.

| Choice | Reason |
|---|---|
| The workflow loads the template in workflow code from the lock's snapshot, with the same `RunCore.Start` re-drive uses | One loader; patches re-run the load checks on the patched plan in the core |
| Every activity awaited by the loop (run start, records) and every step activity is counted for the drain; a confirm read of a child is an activity and is drained too. Confirm reads do not start while draining; the child's end is carried | Nothing may be in flight at a hand-over (C3) |
| Infrastructure errors are thrown from activities and retried by Temporal without limit (1 to 10 seconds apart); work errors are returned and counted by the core | `docs/01` section 4: infrastructure failures never use attempts and never fail a run on their own |
| Records are written by a regular activity after each loop pass: new timeline entries, opened and closed waits, the run's status. Timeline entries are numbered by the run, and the number is carried, so a retried write adds nothing | The API reads only the record store (`docs/02` section 5) |
| A record that closes a wait keeps what the opening record said | The API reads a closed wait's type to authorise a repeated answer |
| A child run's id is `<parent id>.<step id>.<execution>`; a start that finds the child already started adopts it | `docs/04` section 5.1: idempotent by run and step key |
| A `run.child` step cancelled by a decided join or by the parent's end signals the child, which aborts | The parent-close policy no longer ends children |
| `plan.expand` reads the latest artifact of its `from` name, preferring the step's own scope | M3 resolves no `consumes`; std's `tickets.expand` reads `tickets.plan`'s work list |
| The CLI reads a `--param` value as YAML, so `true`, `3` and lists keep their types | Parameters are typed (`docs/03` section 6.1) |
| The fake script keys visits by `<template>:<step id>`, then `<step id>`, then `<template>:*`; `delay_from` and `outcome_from` read a step input | The SP1 scenario needs per-child durations and outcomes |
| `Microsoft.Data.Sqlite` 10.0.14 | 10.0.0 pulls a `SQLitePCLRaw` with a high-severity advisory, which fails the build (warnings are errors) |

## 6. CI

GitHub CI runs on pull request 1 for this branch. The branch is based on `main`
at `11e6aff`. The workflow's commands were also run from a clean clone of
`m3-engine` on macOS, 2026-11-02, first at `c1712c0` and, after the review, at
`d45d812`. Each clone was made inside the working tree as `.ci-clone/` and
deleted afterwards:

| Job | Commands | `c1712c0` | `d45d812` |
|---|---|---|---|
| Build and default tests | `dotnet build FeatureFlow.slnx -c Release`; `dotnet test ... --filter "Category!=Integration&Category!=Live"` | 0 warnings, 0 errors; 277 passed, 0 failed | 0 warnings, 0 errors; 288 passed, 0 failed |
| Integration tests | `temporal server start-dev --headless --port 7833`, with `FF_TEMPORAL_ADDRESS=localhost:7833`; `docker version` (29.9.2); the same build; `dotnet test ... --filter "Category=Integration"` | 9 passed, 0 failed, in 78 seconds | 10 passed, 0 failed, in 79 seconds |

The server was healthy before each integration run and stopped afterwards. The
controls of section 2.4 were run in the working tree with no clone present,
against a separate dev server on port 7933.

## 7. Open questions

| # | Question |
|---|---|
| 1 | LOG 81, 82 and 84 to 90 need review |

Answered: LOG 83, histories holding library text, accepted by the product owner 2026-11-02.

## 8. Follow-ups

Not done in M3:

| # | Follow-up |
|---|---|
| 1 | Check 20 runs on no repository until a provider reads `feature-flow.yaml` from the default branch (M5) |
| 2 | A child receives only `with.parameters`; the parent's resolved parameters for names the child declares (`docs/03` section 9.2) are not passed |
| 3 | `consumes` with `from: parent`, `root` or `child`, and pinning a parent's artifacts at the child's start, are not resolved |
| 4 | The work-item budget (F11) and `run_gate: manual` are not checked |
| 5 | `CleanUp` is recorded on the timeline; resetting the branch is the VCS provider's (M5). `CloseWait` closes the wait's record |
| 6 | DR15 has no control. Once the core is ready, the answer handler applies the answer without awaiting, so removing the hand-over's wait for handlers is not expected to lose an answer here; that was not tried. The wait is kept, as the design requires |
| 7 | A child aborted by its parent is not covered by an integration test |
| 8 | `min_count` in approval rules and roles from an identity provider arrive with M7. A library's `default_approval.auto` is not applied: only organisation rules allow auto-resolution (LOG 89) |
| 9 | Step activities use the default cancellation type, `TryCancel`, so a cancelled step's activity may still be running when the workflow moves on. Change it to `WaitCancellationCompleted` before M4's agent steps, which hold a sandbox |
| 10 | A child that ends without signalling its parent (its signal failed, or it was terminated) never completes the parent's `run.child` step. The parent needs a watch on the child's handle, such as a child-result activity, alongside the signal |
| 11 | A claim-check payload codec (`docs/02` section 5) to restore references-only histories, when history size or data handling requires it |

## 9. Review changes

Requested by the coordinator's review of `5d74726`, 2026-11-02. LOG 83 and the
activity that loads the lock are unchanged; the product owner accepted LOG 83
as built, and `docs/09` section 4.1 and `docs/02` section 5 now say so.

| # | Change |
|---|---|
| 1 | A step's activity that fails with a non-retryable error, and any error applying a step's result (`plan.expand`'s list, the patch, the outputs), reach the core as a work error; a child that cannot be started does too. Before, the step stayed in flight for ever. Test: `A_non_retryable_activity_failure_fails_the_attempt_and_escalates`, with a control |
| 2 | `continue_as_new_events` is at least 100 in the installation file and in a run request; only an administrator may set it per run (LOG 87). The workflow applies the floor too, behind the `m3-hand-over-limits` marker. Tests in `ConfigurationTests` and `ApiTests` |
| 3 | `policy.attempts.hard_ceiling` is read, with defaults of 8 per step and 400 per run. A grant above it is refused unless the API authenticated an administrator (LOG 85). Tests on both sides in `ShellInputTests`, with a control |
| 4 | With no organisation rule for a decision type, the library's `default_approval.roles` decide who may answer, before the fallback (LOG 89). Test in `ApiTests`, with a control. The library default's `auto` is still not applied (follow-up 8) |
| 11 | Wherever a steerer may answer, an administrator may too |
| 12 | A 500 response carries a generic message; the exception goes to the API's log. Test in `ApiTests` |
| 6 | A run also hands over at 8 MB of history (`Workflow.CurrentHistorySize`), behind the same marker as item 2 |
| 7 | `sp1_parent_resumed`, a resumed run carrying unconfirmed child ends, is committed and replayed |
| LOG 81 | The fake providers and the action stand-in are refused unless the installation sets `development: true` |
| 8 to 10 | Recorded as follow-ups 6, 9 and 10 |

The integration tests' thresholds rose to the floor of 100. At 100 the three-ticket
epic handed over only twice, so its work list now has six tickets.

