"""Control runs for the M5 engine actions and checks ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the
source, and runs the same tests again. A control holds when its named tests fail while the rule is broken and
every test passes after the revert. A name matches a failing test when the failing test's name starts with it, so
a theory is named once for all its cases; (name, n) asks for at least n of its cases.

The integration controls need a Temporal dev server (FF_TEMPORAL_ADDRESS, default localhost:7233). No control
needs Docker, a real model or GitHub: they run on the in-memory providers.

Usage, from anywhere:  FF_TEMPORAL_ADDRESS=localhost:8533 python3 tools/check_m5_actions_controls.py [name ...]
Exit status 1 if any control does not hold.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCM = "src/FeatureFlow.Engine/Actions/SourceControl.cs"
WAITING = "src/FeatureFlow.Engine/Actions/Waiting.cs"
TESTING = "src/FeatureFlow.Engine/Actions/Testing.cs"
ACTIONS = "src/FeatureFlow.Engine/Actions/EngineActions.cs"
RESULTS = "src/FeatureFlow.Engine/Actions/ResultFiles.cs"
ACTIVITIES = "src/FeatureFlow.Engine/Activities.cs"
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
RUN_START = "src/FeatureFlow.Validation/RunStartChecks.cs"
AGENT_ACTIVITY = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"

ENGINE = "tests/FeatureFlow.Engine.Tests"
AGENT = "tests/FeatureFlow.Engine.Agent.Tests"
INTEGRATION = "tests/FeatureFlow.Integration.Tests"


def it(name):
    """The integration tests whose name contains this text."""
    return (INTEGRATION, f"FullyQualifiedName~{name}")


CONTROLS = [
    {
        "name": "require_tested: a head with no passing test.run result is merged",
        "edits": [(SCM, 'if (run.Bool("require_tested", true))', 'if (run.Bool("require_tested", true) && false)')],
        "tests": [(ENGINE, None)],
        "expect": ["A_head_without_passing_test_evidence_is_not_merged"],
    },
    {
        "name": "require_checks: a head whose checks failed is merged",
        "edits": [(SCM, 'if (run.Bool("require_checks", true))', 'if (run.Bool("require_checks", true) && false)')],
        "tests": [(ENGINE, None)],
        "expect": ["A_head_with_failed_checks_is_not_merged_and_require_checks_false_lets_it_through"],
    },
    {
        "name": "F1 at merge time: the default branch is not re-read, so the engine merges where the organisation forbids",
        "edits": [(SCM, "var allowed = target != defaultBranch ||", "var allowed = true || target != defaultBranch ||")],
        "tests": [(ENGINE, None)],
        "expect": ["F1_at_merge_time_turns_a_default_branch_merge_the_organisation_forbids_into_an_action_request"],
    },
    {
        "name": "check 23: run start lets a standalone ticket ask for an engine merge into the default branch",
        "edits": [(RUN_START, "if (engineCanMerge == false || RepositoryPatterns.Any(engineMayMerge, repository))",
                   "if (true || engineCanMerge == false || RepositoryPatterns.Any(engineMayMerge, repository))")],
        "tests": [(ENGINE, None), it("ForgeTicketTests.F1_")],
        "expect": ["F1_a_standalone_ticket_asking_for_an_engine_merge_into_the_default_branch_is_refused_at_run_start",
                   "F1_reads_the_default_branch_from_the_code_host_not_main",
                   "F1_through_the_engine_a_standalone_ticket_asking_for_an_engine_merge_into_the_default_branch_does_not_start"],
    },
    {
        "name": "check 24: an unprotected default branch is accepted",
        "edits": [(RUN_START, "        if (!isProtected)\n", "        if (!isProtected && false)\n")],
        "tests": [(ENGINE, None)],
        "expect": ["Check_24_an_unprotected_default_branch_is_refused"],
    },
    {
        "name": "check 20: the repository manifest's library section is not checked",
        "edits": [(ACTIVITIES, 'RunStartChecks.RepositoryLibrary(name, (node as YMap)?["library"], libraries, d);', "_ = node;")],
        "tests": [(ENGINE, None)],
        "expect": ["Check_20_a_repository_library_section_that_overrides_is_refused_and_one_that_adds_passes"],
    },
    {
        "name": "ci.checks: an expected CI run that has not reported is not waited for",
        "edits": [(WAITING, 'pending.AddRange(runs.Runs.Where(r => !Reported(r)).Select(r => $"CI run {r} has not reported"));', "")],
        "tests": [(ENGINE, None)],
        "expect": ["An_early_third_party_check_does_not_pass_while_an_expected_CI_run_has_not_reported"],
    },
    {
        "name": "ci.checks: a head that changes while waiting is not noticed",
        "edits": [(WAITING, "if (waitedOn != head)", "if (waitedOn != head && false)")],
        "tests": [(ENGINE, None)],
        "expect": ["A_head_that_changes_while_checks_are_awaited_raises_an_intervention"],
    },
    {
        "name": "check 31: an action runs with inputs its contract does not allow",
        "edits": [(ACTIONS, "is { Count: > 0 } errors)", "is { Count: > 999 } errors)")],
        "tests": [(ENGINE, None)],
        "expect": ["Check_31_refuses_an_unknown_input_and_a_wrong_type_before_the_action_runs",
                   "Wait_condition_times_out_into_an_intervention_and_takes_one_condition_only"],
    },
    {
        "name": "check 33: a tier tag the manifest does not declare is ignored",
        "edits": [(TESTING, "StepStartChecks.TierTags(repo.FullName, manifest, tags, d);", "_ = d;")],
        "tests": [(ENGINE, None)],
        "expect": ["Check_33_a_tag_that_matches_no_tier_raises_an_intervention_and_no_manifest_does_too"],
    },
    {
        "name": "check 32: a step starts with a consumed artifact no step produced",
        "edits": [(WORKFLOW, "if (Workflow.Patched(StepStartPatch) && MissingConsumed(step) is { } missing)",
                   "if (Workflow.Patched(StepStartPatch) && false && MissingConsumed(step) is { } missing)")],
        "tests": [it("StepStartCheckTests")],
        "expect": ["Check_32_a_step_whose_consumed_artifact_was_never_produced_raises_an_intervention"],
    },
    {
        "name": "test.run: an earlier result is adopted for a different head",
        "edits": [(TESTING, "earlier.Repository == repo.FullName && earlier.Revision == head && !earlier.Baseline",
                   "earlier.Repository == repo.FullName && !earlier.Baseline")],
        "tests": [(ENGINE, None)],
        "expect": ["A_head_already_tested_with_the_same_tiers_adopts_the_result_and_runs_nothing"],
    },
    {
        "name": "TRX: failed tests are counted as passed",
        "edits": [(RESULTS, 'case "Failed" or "Error" or "Timeout" or "Aborted":', 'case "Error":')],
        "tests": [(ENGINE, None)],
        "expect": ["TRX_counts_outcomes_and_keeps_each_failures_first_line", "Failed_tests_are_failed_and_a_baseline_that_is_not_clean_is_baseline_failed"],
    },
    {
        "name": "analysis.run: the baseline is ignored, so old issues fail the run",
        "edits": [(TESTING, "var fresh = counted.Where(i => !known.Contains(i.Fingerprint)).ToList();", "var fresh = counted.ToList();")],
        "tests": [(ENGINE, None)],
        "expect": ["Analysis_fails_on_new_issues_against_the_baseline_and_not_on_old_ones"],
    },
    {
        "name": "clean_up: the work branch is not reset",
        "edits": [(ACTIONS, 'Must(await vcs.PushAsync(repo, reference, start, null, current, ct), "reset the work branch");', "")],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_resets_the_work_branch_to_where_the_step_started_and_keeps_what_it_removes"],
    },
    {
        "name": "clean_up: a later step's hidden reference keeps the discarded commits",
        "edits": [(ACTIONS, "foreach (var step in stepIds)", "foreach (var step in stepIds.Take(0))")],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_resets_the_work_branch_to_where_the_step_started_and_keeps_what_it_removes"],
    },
    {
        "name": "actions: the parent branch is main, not the code host's default branch",
        "edits": [(ACTIONS, 'parentBranches[repo.FullName] = branch = Must(await CodeHost.GetDefaultBranchAsync(repo, ct), "read the default branch");',
                   'parentBranches[repo.FullName] = branch = "main";')],
        "tests": [(ENGINE, None)],
        "expect": ["A_pull_request_is_opened_into_the_default_branch_read_from_the_code_host_and_adopted_on_a_rerun"],
    },
    {
        "name": "agent step: the parent branch is main, not the code host's default branch (M4 follow-up 3)",
        "edits": [(AGENT_ACTIVITY, "spec.ParentBranches.GetValueOrDefault(repo.FullName) ?? await DefaultBranchAsync();",
                   'spec.ParentBranches.GetValueOrDefault(repo.FullName) ?? "main";')],
        "tests": [(AGENT, None)],
        "expect": ["The_parent_branch_is_the_default_branch_the_code_host_names"],
    },
    {
        "name": "agent step: the manifest's setup commands are not run (M4 follow-up 1)",
        "edits": [(AGENT_ACTIVITY, 'await ManifestCommandsAsync(manifest?.Setup ?? [], "setup");', "")],
        "tests": [(AGENT, None)],
        "expect": ["Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_and_is_committed_with_the_step",
                   "A_failing_setup_command_fails_the_attempt_before_the_agent_starts"],
    },
    {
        "name": "agent step: after_changes is not run (M4 follow-up 1)",
        "edits": [(AGENT_ACTIVITY, "if (Writes && head != startRevision && manifest is { AfterChanges.Count: > 0 })",
                   "if (false && Writes && head != startRevision && manifest is { AfterChanges.Count: > 0 })")],
        "tests": [(AGENT, None)],
        "expect": ["Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_and_is_committed_with_the_step"],
    },
]


def run_tests(tests):
    failing = set()
    for project, test_filter in tests:
        command = ["dotnet", "test", project]
        if test_filter:
            command += ["--filter", test_filter]
        run = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
        failing.update(re.findall(r"^\s+Failed [\w.]+\.(\w+(?:\([^)]*\))?) \[", run.stdout, re.M))
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
