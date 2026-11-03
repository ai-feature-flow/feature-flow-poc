"""Control runs for M6 delivery, artifacts and tracker ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the
source, and runs the same tests again. A control holds when its named tests fail while the rule is broken and
every test passes after the revert. A name matches a failing test when the failing test's name starts with it, so
a theory is named once for all its cases; (name, n) asks for at least n of its cases.

The integration controls need a Temporal dev server (FF_TEMPORAL_ADDRESS, default localhost:7233). No control
needs Docker, a real model or GitHub: they run on the in-memory providers and the fake GitHub.

Usage, from anywhere:  FF_TEMPORAL_ADDRESS=localhost:8433 python3 tools/check_m6_delivery_controls.py [name ...]
Exit status 1 if any control does not hold.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DELIVERY = "src/FeatureFlow.Engine/Actions/Delivery.cs"
ACTIONS = "src/FeatureFlow.Engine/Actions/EngineActions.cs"
ARTIFACTS = "src/FeatureFlow.Engine/Actions/Artifacts.cs"
TRACKER = "src/FeatureFlow.Engine/Actions/Tracker.cs"
UPDATES = "src/FeatureFlow.Engine/TrackerUpdates.cs"
WORK_ITEMS = "src/FeatureFlow.Engine/WorkItems.cs"
ACTIVITIES = "src/FeatureFlow.Engine/DeliveryActivities.cs"
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
ISSUES = "src/Providers/FeatureFlow.Providers.GitHub/GitHubIssuesTracker.cs"
RUN_START = "src/FeatureFlow.Validation/RunStartChecks.cs"
CONFIGURATION = "src/FeatureFlow.Model/Configuration.cs"
LOADER = "src/FeatureFlow.Library/PlanLoader.cs"
AGENT = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"
API = "src/FeatureFlow.Api/ApiApp.cs"

ENGINE = ("tests/FeatureFlow.Engine.Tests", None)
AGENT_TESTS = ("tests/FeatureFlow.Engine.Agent.Tests", "FullyQualifiedName~AgentManifestTests")
API_TESTS = ("tests/FeatureFlow.Api.Tests", None)
MODEL = ("tests/FeatureFlow.Model.Tests", "FullyQualifiedName~ConfigurationTests")
REPLAY = ("tests/FeatureFlow.Replay.Tests", None)
GITHUB = ("tests/Providers/FeatureFlow.Providers.GitHub.Tests", "Category!=Live&FullyQualifiedName~Issues")
DELIVERY_RUNS = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~DeliveryRunTests")


CONTROLS = [
    # ---- delivery.report ----
    {
        "name": "delivery.report counts a merge record without confirming the parent branch holds it",
        "edits": [(DELIVERY, 'if (ActionRun.Must(contained, $"check that {target} contains the merge"))',
                   'if (contained.Ok || ActionRun.Must(contained, $"check that {target} contains the merge"))')],
        "tests": [ENGINE],
        "expect": ["A_merge_the_parent_branch_no_longer_contains_is_not_delivered"],
    },
    {
        "name": "delivery.report is complete when any repository merged",
        "edits": [(DELIVERY, "var complete = delivered.All(d => d.Merged);", "var complete = delivered.Any(d => d.Merged);")],
        "tests": [ENGINE],
        "expect": ["Each_named_repository_is_reported_and_one_unmerged_makes_it_partial"],
    },
    {
        "name": "evidence is read from any producer, an agent's merge_record included (F7)",
        "edits": [(ACTIONS, "scope.Artifacts.Where(a => a.Evidence && a.Action is { } action && producers.Contains(action) && TypeName(a.Type) == type)",
                   "scope.Artifacts.Where(a => TypeName(a.Type) == type)")],
        "tests": [ENGINE],
        "expect": ["An_agents_artifact_named_merge_record_is_not_a_merge"],
    },
    # ---- artifact actions ----
    {
        "name": "artifact.import takes a branch or short id as its commit",
        "edits": [(ARTIFACTS, "if (!IsCommit(commit))", "if (commit.Length < 0 && !IsCommit(commit))")],
        "tests": [ENGINE],
        "expect": [("Import_refuses_a_commit_that_is_not_a_full_commit_id", 2)],
    },
    {
        "name": "artifact.import registers a YAML file that does not parse",
        "edits": [(ARTIFACTS, "if (ParseError(path, read.Content) is { } error)", "if (path.Length < 0 && ParseError(path, read.Content) is { } error)")],
        "tests": [ENGINE],
        "expect": ["Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository"],
    },
    {
        "name": "a path with .. is taken as inside the repository",
        "edits": [(ARTIFACTS, 'p.Length == 0 || p is "." or ".."', 'p.Length == 0 || p is "."')],
        "tests": [ENGINE],
        "expect": ["Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository"],
    },
    {
        "name": "artifact.publish ignores a destination changed since the earlier publication",
        "edits": [(ARTIFACTS, "if (now != expected)", "if (now != expected && now == null && expected != null)")],
        "tests": [ENGINE],
        "expect": ["A_destination_changed_since_a_merged_publication_is_a_conflict_and_nothing_is_written"],
    },
    {
        "name": "artifact.publish overwrites a publication branch someone else moved",
        "edits": [(ARTIFACTS, "if (branchHead is not null && branchHead != expectedHead && branchHead != mine)",
                   "if (branchHead is not null && branchHead != expectedHead && branchHead != mine && branchHead.Length < 0)")],
        "tests": [ENGINE],
        "expect": ["A_publication_branch_someone_else_moved_is_a_conflict"],
    },
    {
        "name": "artifact.publish trusts an agent's artifact as the earlier publication",
        "edits": [(ARTIFACTS, 'var entry = Latest(run, name, "artifact.publish")', "var entry = Latest(run, name)")],
        "tests": [ENGINE],
        "expect": ["Previous_must_be_a_publications_own_record"],
    },
    {
        "name": "a retried artifact.publish writes a second commit",
        "edits": [(ARTIFACTS, "if (mine is not null && branchHead == mine)", "if (mine is not null && branchHead == mine && mine.Length < 0)")],
        "tests": [ENGINE],
        "expect": ["A_retried_publication_adopts_its_commit_and_pull_request"],
    },
    {
        "name": "artifact.publish opens a new pull request instead of superseding an open one",
        "edits": [(ARTIFACTS, "var supersede = previous is not null && previousPr?.State == PullRequestStates.Open;",
                   "var supersede = previous is not null && previousPr?.State == PullRequestStates.Open && previous.Branch.Length < 0;")],
        "tests": [ENGINE],
        "expect": ["Republishing_over_an_open_pull_request_supersedes_it_in_place"],
    },
    # ---- tracker.update ----
    {
        "name": "tracker.update creates without the step's key, so a retry makes a second issue",
        "edits": [(TRACKER, 'result = await tracker.CreateAsync(Spec(run) with { Title = Required("title") }, run.Key, run.Ct);',
                   'result = await tracker.CreateAsync(Spec(run) with { Title = Required("title") }, Guid.NewGuid().ToString(), run.Ct);')],
        "tests": [ENGINE],
        "expect": ["Create_makes_one_issue_and_a_retry_of_the_same_step_adopts_it"],
    },
    {
        "name": "tracker.update stops for a person on an issue that does not exist",
        "edits": [(TRACKER, 'if (result is { Ok: false, Error.Reason: "not_found" })', 'if (result is { Ok: false, Error.Reason: "gone" })')],
        "tests": [ENGINE],
        "expect": ["An_issue_that_does_not_exist_is_not_found"],
    },
    {
        "name": "tracker.update does not default to the run's tracker issue",
        "edits": [(TRACKER, 'new(run.String("issue") ?? run.Scope.TrackerIssue', 'new(run.String("issue") ?? (string?)null')],
        "tests": [ENGINE],
        "expect": ["Transition_comment_and_update_act_on_the_runs_issue_by_default"],
    },
    # ---- the GitHub Issues tracker ----
    {
        "name": "the GitHub tracker trusts GitHub's lagging lists alone",
        "edits": [(ISSUES, "for (var i = 0; i < MaxRecent; i++)", "for (var i = 0; i < 0; i++)")],
        "tests": [GITHUB],
        "expect": ["A_create_retried_before_GitHubs_lists_show_the_first_adopts_it", "Creating_twice_with_one_key_creates_one_issue"],
    },
    {
        "name": "a transition keeps the other status labels",
        "edits": [(ISSUES, "var wantLabels = labels.Where(l => !StatusLabels.Contains(l))", "var wantLabels = labels.Where(l => l.Length >= 0)")],
        "tests": [GITHUB],
        "expect": ["A_transition_leaves_one_status_label_and_done_closes_the_issue"],
    },
    {
        "name": "an update drops the hidden marker, so a retried create makes a second issue",
        "edits": [(ISSUES, 'patch["body"] = KeyOf(json["body"]?.GetValue<string>()) is { } key ? WithMarker(body, key) : body;', 'patch["body"] = body;')],
        "tests": [GITHUB],
        "expect": ["An_update_keeps_the_marker_and_the_status_label"],
    },
    {
        "name": "a pull request's number reads as an issue",
        "edits": [(ISSUES, 'return json["pull_request"] is not null ? NotFound<JsonNode>(number.Ref) : ProviderResult<JsonNode>.Success(json);',
                   "return ProviderResult<JsonNode>.Success(json);")],
        "tests": [GITHUB],
        "expect": ["A_pull_requests_number_or_a_deleted_issue_is_not_found"],
    },
    # ---- declarative updates and the run's ticket ----
    {
        "name": "a fragment use's on.<exit> is not seen",
        "edits": [(UPDATES, "r.Outcome == outcome && r.Target is ExitFragment", "r.Outcome == outcome && r.Target is ExitFragment && r.Outcome.Length < 0")],
        "tests": [ENGINE, DELIVERY_RUNS],
        "expect": ["A_step_and_a_fragment_use_move_the_issue_on_start_and_on_an_exit", "Std_epic_delivery_moves_its_issue_to_done_only_when_the_epic_merges",
                   "Declarative_updates_move_the_runs_tracker_issue_as_steps_start_and_a_fragment_leaves"],
    },
    {
        "name": "tracker updates run without their patch marker, so a recorded history no longer replays",
        "edits": [(WORKFLOW, "if (!Workflow.Patched(TrackerUpdatesPatch))", "if (input.RunId.Length < 0 && !Workflow.Patched(TrackerUpdatesPatch))")],
        "tests": [REPLAY],
        "expect": ["A_recorded_history_replays_against_the_current_workflow(name: \"delivery_tracked\")"],
    },
    {
        "name": "the agent step is not given the run's ticket",
        "edits": [(AGENT, "if (spec.TrackerIssue is not { } key)", "if (spec.TrackerIssue is not { } key || key.Length > 0)")],
        "tests": [AGENT_TESTS],
        "expect": ["The_runs_ticket_is_read_from_the_tracker_and_staged_for_the_agent", "A_ticket_the_tracker_does_not_have_stops_the_step_for_a_person"],
    },
    {
        "name": "the API takes any text as a tracker issue",
        "edits": [(API, "if (trackerIssue is not null && (trackerIssue.Length is 0 or > 200 || trackerIssue.Any(char.IsControl)))",
                   "if (trackerIssue is not null && trackerIssue.Length > 200)")],
        "tests": [API_TESTS],
        "expect": [("A_tracker_issue_that_is_not_a_key_is_refused", 2)],
    },
    # ---- the work-item budget (F11) ----
    {
        "name": "a root's budget ignores the organisation's ceiling",
        "edits": [(WORK_ITEMS, "({ } asked, { } ceiling) => new WorkItemBudget(runId, Math.Min(asked, ceiling)),",
                   "({ } asked, { } ceiling) => new WorkItemBudget(runId, Math.Max(asked, ceiling)),")],
        "tests": [ENGINE],
        "expect": ["A_roots_budget_is_its_templates_within_the_ceiling_or_the_ceiling_alone"],
    },
    {
        "name": "the organisation's ceiling is read wrongly",
        "edits": [(CONFIGURATION, 'maxWorkItems = items.Int("ceiling");', 'maxWorkItems = items.Int("ceiling") + 1;')],
        "tests": [MODEL],
        "expect": ["The_work_item_ceiling_is_read_from_policy_budgets"],
    },
    {
        "name": "check 22 lets a template ask for more work items than the ceiling",
        "edits": [(RUN_START, "asked > workItems", "asked > workItems + 1000")],
        "tests": [DELIVERY_RUNS],
        "expect": ["The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more"],
    },
    {
        "name": "run.child starts a child without asking the budget",
        "edits": [(WORKFLOW, "if (budget is not null && Workflow.Patched(WorkItemsPatch)", "if (budget is not null && budget.Limit < 0 && Workflow.Patched(WorkItemsPatch)")],
        "tests": [DELIVERY_RUNS],
        "expect": ["The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count",
                   "The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more",
                   "Children_started_together_are_counted_as_each_is_allowed"],
    },
    {
        "name": "a child gets no budget from its root, so it uses its own",
        "edits": [(WORKFLOW, 'TrackerIssue: inputs.GetValueOrDefault("tracker_issue") as string, WorkItems: budget);',
                   'TrackerIssue: inputs.GetValueOrDefault("tracker_issue") as string, WorkItems: null);')],
        "tests": [DELIVERY_RUNS],
        "expect": ["The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count"],
    },
    {
        "name": "plan.expand adds children past the budget",
        "edits": [(WORKFLOW, "WorkItems.ChildSteps(patch!) is > 0 and var adding", "WorkItems.ChildSteps(patch!) is > 1000 and var adding")],
        "tests": [DELIVERY_RUNS],
        "expect": ["A_plan_expand_past_the_budget_is_refused_before_any_child_starts"],
    },
    {
        "name": "an allowed child is not recorded until it records itself",
        "edits": [(ACTIVITIES, "var written = await services.RecordStore.AppendAsync(new RecordBatch([record]), ct);",
                   "var written = record.RunId.Length >= 0 ? ProviderResult<Unit>.Success(default) : await services.RecordStore.AppendAsync(new RecordBatch([record]), ct);")],
        "tests": [DELIVERY_RUNS],
        "expect": ["Children_started_together_are_counted_as_each_is_allowed"],
    },
    {
        "name": "the budget check runs without its patch marker, so a recorded history no longer replays",
        "edits": [(WORKFLOW, "if (budget is not null && Workflow.Patched(WorkItemsPatch)", "if (budget is not null && budget.Limit > -1")],
        "tests": [REPLAY],
        "expect": ["A_recorded_history_replays_against_the_current_workflow(name: \"budget_middle\")"],
    },
    # ---- for_each ----
    {
        "name": "a for_each over a list known only at run time starts with one representative branch",
        "edits": [(LOADER, "if (known is null && items is not null && options.Repositories is not null)",
                   "if (known is null && items is not null && options.Repositories is not null && items.Text.Length < 0)")],
        "tests": [ENGINE],
        "expect": ["A_for_each_over_a_list_known_only_at_run_time_does_not_start"],
    },
]


def run_tests(tests):
    failing = set()
    for project, test_filter in tests:
        command = ["dotnet", "test", project]
        if test_filter:
            command += ["--filter", test_filter]
        run = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
        failing.update(re.findall(r"^\s+Failed [\w.]+\.(\w+(?:\(.*\))?) \[", run.stdout, re.M))
        if run.returncode != 0 and not failing:
            failing.add("(build or run failed)")
            print(run.stdout[-3000:], flush=True)
    return failing


def matches(expected, failing):
    """A name, or (name, n): at least n failing cases of that theory."""
    name, count = expected if isinstance(expected, tuple) else (expected, 1)
    return sum(1 for f in failing if f == name or f.startswith(name + "(")) >= count


def main():
    wanted = sys.argv[1:]
    ok = True
    for control in CONTROLS:
        if wanted and not any(w in control["name"] for w in wanted):
            continue
        originals = {}
        for path, old, new in control["edits"]:
            full = os.path.join(ROOT, path)
            current = open(full).read()
            originals.setdefault(full, current)
            if current.count(old) != 1:
                print(f"{control['name']}: the text to replace occurs {current.count(old)} times in {path}", flush=True)
                for f, text in originals.items():
                    open(f, "w").write(text)
                sys.exit(1)
            open(full, "w").write(current.replace(old, new))
        try:
            broken = run_tests(control["tests"])
        finally:
            for full, text in originals.items():
                open(full, "w").write(text)
        reverted = run_tests(control["tests"])
        missing = [t for t in control["expect"] if not matches(t, broken)]
        holds = not missing and not reverted
        ok = ok and holds
        print(f"{'ok' if holds else 'DOES NOT HOLD'}: {control['name']}", flush=True)
        print(f"  failing while broken: {sorted(broken)}", flush=True)
        if missing:
            print(f"  expected to fail but passed: {missing}", flush=True)
        print(f"  failing after revert: {sorted(reverted) or 'none'}", flush=True)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
