# M6: findings, validators and levels

Results of building M6 (`docs/11`, M6). Each builder writes its own section; the summary and the consolidated
follow-ups are the integration's.

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

**The App cannot write issues.** The GitHub App installation (`ai-feature-flow-agent-access`) has no `issues`
permission, so a token asking for it is refused (422). The provider asks for exactly `issues: write` and
`metadata: read`, so with the App it stops for a person as a configuration fault. The Live contract therefore ran with
`FF_GITHUB_TRACKER_TOKEN` naming the approved personal token. Granting the App "Issues: read and write" is the
product owner's decision (open question 1 of the report).

**Bugs the tests found.** The first live run failed twice: GitHub's issue lists lag a create by about five seconds
(measured), so adoption by marker made a second issue and a label search found nothing. Searches now read the issue
numbers above the newest listed one directly, and match labels themselves. The integration tests found two races in
declarative updates: a step that ended while an update was awaited, or between the last update and the record, was
recorded without being examined (2 failures in 5 runs before the second fix, 0 in 6 after).

### Controls

`tools/check_m6_delivery_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker, model or GitHub). Each breakage was run, its tests failed, and all passed again after the revert. **All 31 hold.** One did not at first: a retried `artifact.publish` re-made an identical commit, which the memory forge names by its content, so the test passed; it now also checks that the retry opens no sandbox.

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

### Deviations and judgement calls

- LOG 180 to 188, all unreviewed. The ones marked "Needs a look": 181 (no schema check on import), 184 (the App's
  missing `issues` permission), 185 (no comments in the staged ticket), 186 (guarded exits not seen; refused
  updates noted, not retried as docs/03 said), 187 (the concurrent-creation race; unlimited with no budget and no
  ceiling).
- `run.child` gained an optional `tracker_issue` input (LOG 185).

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Grant the GitHub App `issues: write`, or run the tracker with another identity (product owner) |
| 2 | Schema validation of imported artifacts against their types (LOG 181); refusing evidence types on `artifact.import` at load belongs with check 12 for actions |
| 3 | std's `plan_children` does not pass a work list item's `tracker_issue` to `run.child`: `plan.expand` cannot substitute an optional field yet |
| 4 | The staged ticket has no comments: `ITrackerProvider` reads none |
| 5 | Declarative `on.<exit>` through a guarded route; nested fragment exits |
| 6 | Two parents of one tree creating children at the same instant can each pass the last free work item (LOG 187) |
| 7 | `wait.condition` on a tracker issue (M5 follow-up 8) can now use the tracker provider |
