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
STEP_START = "src/FeatureFlow.Validation/StepStartChecks.cs"
API = "src/FeatureFlow.Api/ApiApp.cs"
AGENT_ACTIVITY = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"
CORE = "src/FeatureFlow.Interpreter/RunCore.cs"
RECOVERY = "src/FeatureFlow.Interpreter/Recovery.cs"
VALIDATOR = "src/FeatureFlow.Validation/PlanValidator.cs"
FAULTS = "src/FeatureFlow.Engine/Actions/ConfigurationFaults.cs"

ENGINE = "tests/FeatureFlow.Engine.Tests"
AGENT = "tests/FeatureFlow.Engine.Agent.Tests"
CORE_TESTS = "tests/FeatureFlow.Interpreter.Tests"
VALIDATION_TESTS = "tests/FeatureFlow.Validation.Tests"
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
        "edits": [(RESULTS, 'case "Passed":', 'case "Passed" or "Failed":')],
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
    {
        "name": "LOG 137: a start's parent branch is dropped, so the run works on and merges into the default branch",
        "edits": [(API, "HandOverEvents: handOver, ParentBranch: parentBranch);", "HandOverEvents: handOver);")],
        "tests": [it("A_ticket_started_with_a_parent_branch")],
        "expect": ["A_ticket_started_with_a_parent_branch_lands_there_by_an_engine_merge_and_main_is_untouched"],
    },
    {
        "name": "LOG 137: any named parent branch skips F1, even the default branch",
        "edits": [(ACTIVITIES, "(start.ParentBranch ?? branch) == branch", "start.ParentBranch is null")],
        "tests": [(ENGINE, None)],
        "expect": ["F1_still_applies_when_a_start_names_the_default_branch_as_the_parent_branch"],
    },
    # Added for the review of PR 6 (docs/build/m5-results.md).
    {
        "name": "review 1: merge evidence is found by its type's name alone",
        "edits": [(ACTIONS, "scope.Artifacts.Where(a => a.Evidence && a.Action is { } action && producers.Contains(action) && TypeName(a.Type) == type)",
                   "scope.Artifacts.Where(a => TypeName(a.Type) == type)")],
        "tests": [(ENGINE, None)],
        "expect": [("Evidence_that_no_testing_action_produced_is_not_merged", 3), "Test_integration_results_do_not_satisfy_require_tested"],
    },
    {
        "name": "review 1: the workflow marks no artifact as evidence, so nothing can merge",
        "edits": [(WORKFLOW, "var evidence = type?.Reference is { } typeRef && libraries?.Resolve(typeRef)?.ArtifactType(typeRef.Name)?.Evidence == true;",
                   "var evidence = false;")],
        "tests": [it("A_std_ticket_runs_on_the_memory_providers")],
        "expect": ["A_std_ticket_runs_on_the_memory_providers_to_an_engine_merge_into_its_parent_branch"],
    },
    {
        "name": "review 8 B1: merge evidence for another repository counts",
        "edits": [(SCM, "t.Value.Repository == repo.FullName && t.Value.Revision == head && t.Value.Passed", "t.Value.Revision == head && t.Value.Passed")],
        "tests": [(ENGINE, None)],
        "expect": ["Evidence_for_another_repository_at_the_same_revision_is_not_merged"],
    },
    {
        "name": "review 2: TRX outcomes other than Passed count as skipped",
        "edits": [(RESULTS, 'case "NotExecuted":', 'case "NotExecuted" or "PassedButRunAborted" or "NotRunnable" or "Inconclusive" or "Timeout":')],
        "tests": [(ENGINE, None)],
        "expect": [("A_TRX_outcome_other_than_passed_or_not_executed_fails", 4)],
    },
    {
        "name": "review 2: a TRX run's own outcome is ignored",
        "edits": [(RESULTS, "if (run is not null and not", "if (false && run is not null and not")],
        "tests": [(ENGINE, None)],
        "expect": ["A_TRX_run_that_aborted_fails_even_when_every_result_passed"],
    },
    {
        "name": "review 2: JUnit suite-level errors and counts are ignored",
        "edits": [(RESULTS, 'foreach (var suite in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testsuite"))',
                   'foreach (var suite in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "never"))')],
        "tests": [(ENGINE, None)],
        "expect": ["A_JUnit_suite_level_error_or_unattributed_failure_count_fails"],
    },
    {
        "name": "review 2: a tier that ran no tests passes",
        "edits": [(TESTING, "results.Passed == 0 && results.Failed == 0\n", "results.Passed < 0 && results.Failed == 0\n")],
        "tests": [(ENGINE, None)],
        "expect": [("A_tier_that_ran_no_tests_is_not_a_pass", 2)],
    },
    {
        "name": "review 3: a SARIF entry of the wrong kind escapes as a non-format error",
        "edits": [(RESULTS, "catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)",
                   "catch (Exception e) when (e is KeyNotFoundException)")],
        "tests": [(ENGINE, None)],
        "expect": [("A_SARIF_file_without_runs_does_not_parse", 4)],
    },
    {
        "name": "review 3: an unexpected exception in an action escapes, so Temporal retries it for ever",
        "edits": [(ACTIONS, "catch (Exception e) when (e is not (ActionInfrastructureException or OperationCanceledException))",
                   "catch (Exception e) when (e is ArithmeticException)")],
        "tests": [(ENGINE, None)],
        "expect": ["An_unexpected_exception_in_an_action_raises_an_intervention_rather_than_a_retry"],
    },
    {
        "name": "review 4: only a failure conclusion fails (the old deny-list, narrowed)",
        "edits": [(WAITING, "return list.Where(c => !Passing.Contains(c.Conclusion!))", "return list.Where(c => c.Conclusion == Conclusions.Failure)")],
        "tests": [(ENGINE, None)],
        "expect": [("Only_success_neutral_and_skipped_pass", 5)],
    },
    {
        "name": "review 4: an unknown conclusion is judged instead of raising an intervention",
        "edits": [(WAITING, "if (list.FirstOrDefault(c => !Passing.Contains", "if (false && list.FirstOrDefault(c => !Passing.Contains")],
        "tests": [(ENGINE, None)],
        "expect": ["A_conclusion_the_engine_does_not_know_raises_an_intervention"],
    },
    {
        "name": "review 5: a poll interval of any length passes check 31",
        "edits": [(STEP_START, "else if (input.Minimum is { } minimum && value is string d && Duration(d) < minimum)",
                   "else if (input.Minimum is { } minimum && value is string d && Duration(d) < TimeSpan.Zero)")],
        "tests": [(ENGINE, None)],
        "expect": [("Check_31_refuses_a_poll_interval_under_five_seconds", 3)],
    },
    {
        "name": "review 9: a clean_up work error is thrown, to be retried for ever",
        "edits": [(ACTIONS, "        catch (ActionStop stop)\n        {\n            return new CleanUpResult(events, stop.Message);",
                   "        catch (ActionStop stop) when (stop.Kind == \"never\")\n        {\n            return new CleanUpResult(events, stop.Message);")],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_whose_push_is_refused_returns_the_error_and_moves_nothing"],
    },
    {
        "name": "review 8 B3: clean_up resets to where the first execution started, not the latest",
        "edits": [(ACTIONS, "for (var n = executions; n >= 1 && starts.Count == 0; n--)", "for (var n = 1; n <= executions && starts.Count == 0; n++)")],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_resets_to_where_the_latest_execution_of_the_step_started"],
    },
    {
        "name": "review 10: tier commands and result paths come from the agent-written head",
        "edits": [(TESTING, "var evidence = await run.ManifestAsync(repo, at)", "var evidence = await run.ManifestAsync(repo, head)")],
        "tests": [(ENGINE, None)],
        "expect": ["Tier_commands_and_result_paths_come_from_the_parent_branchs_manifest_not_the_heads"],
    },
    {
        "name": "review 10: a result file already in the checkout is read as this run's",
        "edits": [(TESTING, "if ((await ReadFilesAsync(path)).Count > 0)", "if (false)")],
        "tests": [(ENGINE, None)],
        "expect": ["A_result_file_already_in_the_checkout_that_cannot_be_removed_raises_an_intervention"],
    },
    {
        "name": "review 11: actions write to a work branch that is the parent or default branch",
        "edits": [(ACTIONS, "if (scope.WorkBranch == parent || scope.WorkBranch == defaultBranch)", "if (false)")],
        "tests": [(ENGINE, None)],
        "expect": [("No_action_writes_when_the_work_branch_is_the_parent_or_default_branch", 6)],
    },
    {
        "name": "review 11: run start accepts a work branch that is the default or parent branch",
        "edits": [(ACTIVITIES, "if (work == defaultBranch || work == start.ParentBranch)", "if (work == \"never a branch\")")],
        "tests": [(ENGINE, None)],
        "expect": ["A_work_branch_that_is_the_default_or_parent_branch_is_refused_at_run_start"],
    },
    {
        "name": "review 11: a clean_up resets the default branch",
        "edits": [(ACTIONS, '&& Must(await host.GetDefaultBranchAsync(repo, ct), "read the default branch") == workBranch)',
                   '&& Must(await host.GetDefaultBranchAsync(repo, ct), "read the default branch") == workBranch && false)')],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_never_resets_the_default_branch"],
    },
    {
        "name": "review 11: an agent step works on the default branch",
        "edits": [(AGENT_ACTIVITY, "if (workBranch == parent || (owner.Providers", 'if (workBranch == "never" && (owner.Providers')],
        "tests": [(AGENT, None)],
        "expect": ["An_agent_step_whose_work_branch_is_the_default_branch_does_not_start"],
    },
    {
        "name": "review 8: the workflow never runs a clean_up reset",
        "edits": [(WORKFLOW, "case CleanUp c when Workflow.Patched(CleanUpPatch):", "case CleanUp c when c.StepId == \"never a step\":")],
        "tests": [it("A_rejected_specification_resets_the_work_branch_to_where_planning_started")],
        "expect": ["A_rejected_specification_resets_the_work_branch_to_where_planning_started"],
    },
    {
        "name": "review 9: a refused clean_up is ignored and the step runs on the branch that was not reset",
        "edits": [(WORKFLOW, "if (cleanUpFailures.Remove(step.Id, out var cleanUpError))", "if (false && cleanUpFailures.Remove(step.Id, out var cleanUpError))")],
        "tests": [it("A_clean_up_the_remote_refuses")],
        "expect": ["A_clean_up_the_remote_refuses_raises_an_intervention_at_the_step_it_was_for"],
    },
    {
        "name": "review 13: a built action served by the stand-in gets a heartbeat timeout",
        "edits": [(WORKFLOW, "&& policy?.BuiltActions == true && !input.DryRun)", "&& !input.DryRun)")],
        "tests": [it("Only_an_action_run_against_providers_has_a_heartbeat_timeout")],
        "expect": ["Only_an_action_run_against_providers_has_a_heartbeat_timeout"],
    },
    # Added for the re-check of PR 6.
    {
        "name": "re-check R1: a network fault a provider lets escape stops the step for a person",
        "edits": [(ACTIONS, "catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException or TimeoutException)",
                   "catch (Exception e) when (e is ArithmeticException)")],
        "tests": [(ENGINE, None)],
        "expect": [("A_network_fault_a_provider_lets_escape_is_retried_as_infrastructure", 4)],
    },
    {
        "name": "re-check R3: a clean_up aimed at a container is lost",
        "edits": [(CORE, "State.Pending.Add(new Entry(branch.Steps[0].Id, cleanUpFirst));", "State.Pending.Add(new Entry(branch.Steps[0].Id, false));")],
        "tests": [(CORE_TESTS, None)],
        "expect": ["A_clean_up_aimed_at_a_container_is_the_clean_up_of_its_first_step"],
    },
    {
        "name": "re-check R3: a clean_up route aimed at a decision loads",
        "edits": [(VALIDATOR, "if (byId.GetValueOrDefault(target.StepId) is { Form: StepForm.Decision })", 'if (byId.GetValueOrDefault(target.StepId) is { Id: "never a step" })')],
        "tests": [(VALIDATION_TESTS, None)],
        "expect": ["Mutation_triggers_its_check_and_no_other(name: \"clean_up_to_a_decision\")"],
    },
    {
        "name": "re-check R3: a person may go to a decision with clean_up",
        "edits": [(RECOVERY, "return action.CleanUp && StepById(target)?.Form == StepForm.Decision", 'return action.CleanUp && StepById(target)?.Id == "never a step"')],
        "tests": [(CORE_TESTS, None)],
        "expect": ["A_person_may_not_go_to_a_decision_with_clean_up"],
    },
    {
        "name": "re-check R4: an evidence sandbox runs the head's setup commands",
        "edits": [(TESTING, "        return (evidence, evidence.Setup);", "        return (evidence, at == head ? evidence.Setup : (await run.ManifestAsync(repo, head))?.Setup ?? []);")],
        "tests": [(ENGINE, None)],
        "expect": ["Tier_commands_and_result_paths_come_from_the_parent_branchs_manifest_not_the_heads"],
    },
    {
        "name": "check 24: protection whose review rules are unknown passes silently",
        "edits": [(RUN_START, "        else if (!reviewRulesKnown)\n", "        else if (!reviewRulesKnown && repository == \"never a repository\")\n")],
        "tests": [(ENGINE, None)],
        "expect": ["Check_24_protection_whose_review_rules_are_unknown_passes_with_a_warning"],
    },
    {
        "name": "check 24: run start's warnings never reach the timeline",
        "edits": [(WORKFLOW, "if (prepared.Warnings.Count > 0 && Workflow.Patched(StartWarningsPatch))", "if (prepared.Warnings.Count > 999 && Workflow.Patched(StartWarningsPatch))")],
        "tests": [it("Protection_with_unknown_review_rules")],
        "expect": ["Protection_with_unknown_review_rules_starts_the_run_with_a_warning_on_its_timeline"],
    },
    # Added for the engine-level SP4 (LOG 117's follow-up).
    {
        "name": "engine SP4: scm.merge reads no evidence, so an untested head lands in the race",
        "edits": [(SCM, "    static async Task<string?> MissingEvidenceAsync(ActionRun run, RepoRef repo, string head)\n    {\n",
                   "    static async Task<string?> MissingEvidenceAsync(ActionRun run, RepoRef repo, string head)\n    {\n        if (head.Length > 0)\n            return null;\n")],
        "tests": [it("EngineMergeRaceTests")],
        "expect": ["N2_a_merge_after_a_changing_catch_up_without_a_re_test_is_refused_by_the_engine"],
    },
    {
        "name": "engine SP4: scm.merge reports a refused merge as merged",
        "edits": [(SCM, '                return run.Report("base_moved");', '                return await RecordAsync(run, repo, target, method, head, targetRevision, targetRevision, EngineActor, false, pr);')],
        "tests": [it("EngineMergeRaceTests")],
        "expect": ["S1_three_tickets_forced_to_collide_all_merge_through_the_engine_and_none_is_lost"],
    },
    # Added for the budget of forbidden and unauthenticated (docs/06 section 1.1).
    {
        "name": "configuration faults: a refused credential has no budget and is retried for ever",
        "edits": [(FAULTS, 'public static bool Is(string? reason) => reason is "unauthenticated" or "forbidden";', 'public static bool Is(string? reason) => reason is "never a reason";')],
        "tests": [(ENGINE, None)],
        "expect": [("A_refused_credential_gets_a_short_budget_then_stops_the_step_for_a_person", 2),
                   "A_clean_up_whose_credential_is_refused_stops_for_a_person_after_the_budget",
                   ("A_refused_credential_at_run_start_fails_the_start_naming_the_configuration_fault_once_its_budget_is_spent", 3)],
    },
    {
        "name": "configuration faults: the step stops at the first refusal, with no retry",
        "edits": [(FAULTS, "                if (tries >= Tries)\n                    return exhausted", "                if (tries >= 1)\n                    return exhausted")],
        "tests": [(ENGINE, None)],
        "expect": ["A_refused_credential_that_recovers_within_the_budget_does_not_stop_the_step"],
    },
    # Added for the review of PR 7.
    {
        "name": "configuration faults: run start refuses at the first refusal, with no retry",
        "edits": [(FAULTS, "if (!Is(result.Error) || ++budget.Refusals >= Tries)", "if (!Is(result.Error) || ++budget.Refusals >= 1)")],
        "tests": [(ENGINE, None)],
        "expect": [("A_refused_credential_at_run_start_fails_the_start_naming_the_configuration_fault_once_its_budget_is_spent", 3),
                   "Refused_reads_across_many_repositories_share_one_budget_so_the_start_is_refused_within_it"],
    },
    {
        "name": "configuration faults: each run-start read has its own budget",
        "edits": [(ACTIVITIES, "Current.Heartbeat(); }, ct, budget);", "Current.Heartbeat(); }, ct);")],
        "tests": [(ENGINE, None)],
        "expect": ["Refused_reads_across_many_repositories_share_one_budget_so_the_start_is_refused_within_it"],
    },
    {
        "name": "configuration faults: a spent clean_up budget drops the resets already made",
        "edits": [(ACTIONS, "message => new CleanUpResult(events.ToList(), ", "message => new CleanUpResult([], ")],
        "tests": [(ENGINE, None)],
        "expect": ["A_clean_up_refused_in_a_later_repository_still_reports_the_resets_it_made_before"],
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
