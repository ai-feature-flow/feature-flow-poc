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

## Delivery, artifacts and tracker

Branch `m6-delivery`, from `m6@c238b4d` (the shared interfaces, PR 9). Worker W4. Judgement calls: LOG 180 to 188,
unreviewed.

### What was built

| Part | Where | What |
|---|---|---|
| `delivery.report` | `Engine/Actions/Delivery.cs` | Per repository: the run's `merge_record` made by `scm.merge` into the parent branch, counted only if the branch still contains it. `complete` only if every repository merged. Outputs `merged`, `not_merged`; a `DeliveryReport` when declared (LOG 180) |
| `artifact.compose` | `Engine/Actions/Artifacts.cs` | Named artifacts into an `ArtifactSet` at their exact references |
| `artifact.import` | same | Files from a repository at a full commit id, each the artifact it names; YAML and JSON must parse; an `ImportRecord` (LOG 181). The session form is M7's |
| `artifact.publish` | same | One commit through an engine sandbox on `ff/publish/<run>/<step>/<execution>`, a pull request into the default branch; `conflict` (nothing written) when the destination or the branch moved; supersedes an open earlier publication in place; a `Publication` record (LOG 182) |
| `tracker.update` | `Engine/Actions/Tracker.cs` | `create`, `update`, `transition`, `link`, `comment`; the issue defaults to the run's; `not_found` (LOG 183) |
| GitHub Issues tracker | `Providers.GitHub/GitHubIssuesTracker.cs` | Kind `github_issues`: statuses as labels or `closed`, parents as sub-issues, links as keyed comments, no custom fields; reads past GitHub's list lag (LOG 184) |
| The run's ticket | API, CLI, `Wire.cs`, `Engine.Agent/AgentActivities.cs` | `tracker_issue` on the run request and on `run.child`; each agent step stages the issue as `/ff/inputs/ticket.md`; std's tasks read it (LOG 185). Replaces M5's ticket file (LOG 146) |
| Declarative tracker updates | `Engine/TrackerUpdates.cs`, `RunWorkflow.cs` | From the timeline before each record: steps' and fragment uses' `on_start` and `on` (LOG 186). Patch `m6-tracker-updates` |
| Work-item budget (F11) | `Engine/WorkItems.cs`, `DeliveryActivities.cs`, `RunWorkflow.cs`, `Configuration.cs`, `RunStartChecks.cs` | The root's budget within `policy.budgets.max_work_items.ceiling` (check 22); counted in the record store before `run.child` and `plan.expand`, an allowed child recorded at once (LOG 187). Patch `m6-work-items` |
| `for_each` | `Library/PlanLoader.cs` | A list not known when the run starts is refused (check 5), not run as one empty branch (LOG 188). Load-time expansion over `run.repositories` was already M1's |

Changes to W1's files are kept small and marked: `RunWorkflow.cs` (the action scope and agent spec carry the
tracker issue; `RecordAsync` asks for tracker updates; `StartChildAsync` and `plan.expand` ask the budget; one
"Delivery" region), `Activities.cs` (`partial`, and `RunInfoOf` passes the tracker issue), `Wire.cs` (two trailing
`RunStart` fields, one `RunPolicy` field, `WorkItemBudget`). Shared test harness: `ActionWorld` gained a tracker,
the tracker issue and declared produces.

### Checks

| What | Test | Result |
|---|---|---|
| `delivery.report` | `Engine.Tests/DeliveryActionTests` (6) | Pass |
| Artifact actions | `Engine.Tests/ArtifactActionTests` (14) | Pass |
| `tracker.update` | `Engine.Tests/TrackerActionTests` (9) | Pass |
| Tracker updates due, budget values, `for_each` at run start | `Engine.Tests/TrackerUpdateAndBudgetTests` (4) | Pass |
| GitHub Issues tracker | `GitHubIssuesTrackerContract` (`TrackerContract` over a fake GitHub whose lists lag) and `GitHubIssuesTrackerTests`: 19 | Pass |
| GitHub Issues, live | `GitHubIssuesTrackerLiveContract` (Live) on the fixture: 9, every issue closed after | Pass, with the approved token (see below) |
| Ticket staged for the agent | `Engine.Agent.Tests/AgentManifestTests` (2 new) | Pass |
| `tracker_issue` in the API, the ceiling in configuration | `ApiTests` (2 cases), `ConfigurationTests` (4 cases) | Pass |
| Through the engine | `Integration.Tests/DeliveryRunTests` (6): budget across a tree, children started together, `plan.expand` refused whole, the organisation ceiling and check 22, tracker updates through a fragment exit, a failing update noted | Pass |
| Replay | `delivery_tracked` and `budget_middle` histories, each holding its patch marker | Pass |

**The App's issues permission.** At first the App installation had no `issues` permission, so the first Live runs used
the approved personal token (`FF_GITHUB_TRACKER_TOKEN`). The product owner has since granted it; see the review section.

**Bugs the tests found.** The first live run failed twice: GitHub's issue lists lag a create by about five seconds
(measured), so adoption by marker made a second issue and a label search found nothing. Searches now read the issue
numbers above the newest listed one directly, and match labels themselves. The integration tests found two races in
declarative updates: a step that ended while an update was awaited, or between the last update and the record, was
recorded without being examined (2 failures in 5 runs before the second fix, 0 in 6 after).

### Controls

`tools/check_m6_delivery_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker, model or GitHub). Each breakage was run, its tests failed, and all passed again after the revert. **All 45 hold** (31 first, 14 added for the review of PR 12). One did not at first: a retried `artifact.publish` re-made an identical commit, which the memory forge names by its content, so the test passed; it now also checks that the retry opens no sandbox.

| Breakage | Tests that failed |
|---|---|
| Delivery.report counts a merge record without confirming the parent branch holds it | `A_merge_the_parent_branch_no_longer_contains_is_not_delivered` |
| Delivery.report is complete when any repository merged | `Each_named_repository_is_reported_and_one_unmerged_makes_it_partial` |
| Evidence is read from any producer, an agent's merge_record included (F7) | `An_agents_artifact_named_merge_record_is_not_a_merge` |
| Artifact.import takes a branch or short id as its commit | `Import_refuses_a_commit_that_is_not_a_full_commit_id` |
| Artifact.import registers a YAML file that does not parse | `Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository` |
| A path with .. is taken as inside the repository | `Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository` |
| Artifact.publish ignores a destination changed since the earlier publication | `A_destination_changed_since_a_merged_publication_is_a_conflict_and_nothing_is_written` |
| Artifact.publish overwrites a publication branch someone else moved | `A_publication_branch_someone_else_moved_is_a_conflict` |
| Artifact.publish trusts an agent's artifact as the earlier publication | `Previous_must_be_a_publications_own_record` |
| A retried artifact.publish writes a second commit | `A_retried_publication_adopts_its_commit_and_pull_request` |
| Artifact.publish opens a new pull request instead of superseding an open one | `Republishing_over_an_open_pull_request_supersedes_it_in_place` |
| Tracker.update creates without the step's key, so a retry makes a second issue | `Create_makes_one_issue_and_a_retry_of_the_same_step_adopts_it` |
| Tracker.update stops for a person on an issue that does not exist | `An_issue_that_does_not_exist_is_not_found` |
| Tracker.update does not default to the run's tracker issue | `Transition_comment_and_update_act_on_the_runs_issue_by_default` |
| The GitHub tracker trusts GitHub's lagging lists alone | `A_create_retried_before_GitHubs_lists_show_the_first_adopts_it`, `Creating_twice_with_one_key_creates_one_issue` |
| A transition keeps the other status labels | `A_transition_leaves_one_status_label_and_done_closes_the_issue` |
| An update drops the hidden marker, so a retried create makes a second issue | `An_update_keeps_the_marker_and_the_status_label` |
| A pull request's number reads as an issue | `A_pull_requests_number_or_a_deleted_issue_is_not_found` |
| A fragment use's on.<exit> is not seen | `A_step_and_a_fragment_use_move_the_issue_on_start_and_on_an_exit`, `Std_epic_delivery_moves_its_issue_to_done_only_when_the_epic_merges`, `Declarative_updates_move_the_runs_tracker_issue_as_steps_start_and_a_fragment_leaves` |
| Tracker updates run without their patch marker, so a recorded history no longer replays | `replay of delivery_tracked` |
| The agent step is not given the run's ticket | `The_runs_ticket_is_read_from_the_tracker_and_staged_for_the_agent`, `A_ticket_the_tracker_does_not_have_stops_the_step_for_a_person` |
| The API takes any text as a tracker issue | `A_tracker_issue_that_is_not_a_key_is_refused` |
| A root's budget ignores the organisation's ceiling | `A_roots_budget_is_its_templates_within_the_ceiling_or_the_ceiling_alone` |
| The organisation's ceiling is read wrongly | `The_work_item_ceiling_is_read_from_policy_budgets` |
| Check 22 lets a template ask for more work items than the ceiling | `The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more` |
| Run.child starts a child without asking the budget | `The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count`, `The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more`, `Children_started_together_are_counted_as_each_is_allowed` |
| A child gets no budget from its root, so it uses its own | `The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count` |
| Plan.expand adds children past the budget | `A_plan_expand_past_the_budget_is_refused_before_any_child_starts` |
| An allowed child is not recorded until it records itself | `Children_started_together_are_counted_as_each_is_allowed` |
| The budget check runs without its patch marker, so a recorded history no longer replays | `replay of budget_middle` |
| A for_each over a list known only at run time starts with one representative branch | `A_for_each_over_a_list_known_only_at_run_time_does_not_start` |

Not in the script: the race in `RecordAsync` (the previous code failed the integration test 2 times in 5; a
deterministic mutation would need a scheduling hook).

### Review of PR 12 (changes-required) and fixes

| Finding | Fix | Commit |
|---|---|---|
| F1 (high): a stale merge record read as delivered after more work | A merge counts only for the work branch's current head (LOG 180) | `81c1dc5` |
| F2: a reverted merge read as delivered | `scm.revert` produces `revert_record` evidence (new std engine type); a merge whose revert is merged or open is not delivered | `81c1dc5` |
| F3: check 22 refused a child whose own budget was above the ceiling | The F11 cap applies to roots only (LOG 187) | `6b6e143` |
| F4: a tracker outage stalled the run | Five tries over about 30 s, then a timeline note (LOG 186) | `6b6e143` |
| F5 to F8 (low) | LOG 188 says why no patch marker; a failed child start stays counted (LOG 187); LOG 187 reworded on agents and siblings; comment-list lag noted (LOG 184) | `6b6e143` |
| 8 surviving mutations | Tests for 7; dropping the empty-revision filter is equivalent on the memory VCS (an empty revision reads as not found, which is skipped) | `81c1dc5`, `6b6e143` |

After merging `m6` with W1's PR 10 (`beb311b`): build 0 warnings, default suite 834 passed, integration 65 passed,
controls 45 of 45 hold (run before the merge; the merge touched no line they mutate except beside `StartChildAsync`,
whose budget path the integration tests cover).

Product owner decisions (2026-11-04): the App has "Issues: read and write". The broker's engine ceiling now includes
`issues: write`, and agents still get no issues access (`201f981`). The Live contract ran again with the App's credential:
9 passed, and every issue was made by `ai-feature-flow-agent-access[bot]`, closed by the test, then deleted. Open question
2: a `plan.expand` path ending in `?` is optional, and std's `plan_children` passes `$.tracker_issue?` (LOG 185). Open
question 3 was accepted as recommended.

### Deviations and judgement calls

- LOG 180 to 188, all unreviewed. The ones marked "Needs a look": 181 (no schema check on import), 184 (the App's
  missing `issues` permission), 185 (no comments in the staged ticket), 186 (guarded exits not seen; refused
  updates noted, not retried as docs/03 said), 187 (the concurrent-creation race; unlimited with no budget and no
  ceiling).
- `run.child` gained an optional `tracker_issue` input (LOG 185).

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Schema validation of imported artifacts against their types (LOG 181); refusing evidence types on `artifact.import` at load belongs with check 12 for actions |
| 2 | The staged ticket has no comments: `ITrackerProvider` reads none |
| 3 | Declarative `on.<exit>` through a guarded route; nested fragment exits |
| 4 | Two runs of one tree creating children at the same instant can each pass the last free work item (LOG 187); a child that fails to start stays counted |
| 5 | `wait.condition` on a tracker issue (M5 follow-up 8) can now use the tracker provider |
| 6 | No engine default ceiling: with no budget and no organisation ceiling a tree is unlimited (accepted; set a ceiling in shipped profiles) |
