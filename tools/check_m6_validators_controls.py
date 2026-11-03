"""Control runs for M6's validators and evidence ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the source,
and runs the same tests again. A control holds when its named tests fail while the rule is broken and every test
passes after the revert. A name matches a failing test when the failing test's name starts with it, so a theory is
named once for all its cases; (name, n) asks for at least n of its cases.

No control needs Temporal, Docker, a model or GitHub: the validators and artifact.validate are tested on the in-memory
providers and a local git repository.

Usage, from anywhere:  python3 tools/check_m6_validators_controls.py [name ...]
Exit status 1 if any control does not hold.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STD = "src/Providers/FeatureFlow.Providers.StdValidators"
REQUIREMENTS = f"{STD}/Requirements.cs"
EVIDENCE = f"{STD}/Evidence.cs"
PROVIDER = f"{STD}/StdValidators.cs"
VALIDATE = "src/FeatureFlow.Engine/Actions/Validation.cs"
ACTIONS = "src/FeatureFlow.Engine/Actions/EngineActions.cs"
RESULTS = "src/FeatureFlow.Engine/Actions/ResultFiles.cs"
CEL = "src/FeatureFlow.Expressions/CelExpressions.cs"
VALIDATOR = "src/FeatureFlow.Validation/PlanValidator.cs"

STD_TESTS = ("tests/Providers/FeatureFlow.Providers.StdValidators.Tests", None)
ENGINE = ("tests/FeatureFlow.Engine.Tests", "FullyQualifiedName~ValidationActionTests|FullyQualifiedName~ResultFileTests")
EXPRESSIONS = ("tests/FeatureFlow.Expressions.Tests", None)
VALIDATION_TESTS = ("tests/FeatureFlow.Validation.Tests", "FullyQualifiedName~MutationTests")
STD_WORKFLOWS = ("tests/FeatureFlow.Testing.Workflows.Tests", "FullyQualifiedName~Std_workflow_test_passes")
FIX_LOOP = "library/std/fragments/fix_loop.yaml"

FIXTURES = "Every_fixture_gets_its_verdict_and_the_verdict_agrees_with_the_issues"
INTEGRATION = "tests/FeatureFlow.Integration.Tests"


def it(name):
    """The integration tests whose name contains this text (Temporal at FF_TEMPORAL_ADDRESS)."""
    return (INTEGRATION, f"FullyQualifiedName~{name}")


def warn(path, code):
    """An error made a warning: its fixture must then come out valid."""
    return (path, f'Error("{code}"', f'Warn("{code}"')

CONTROLS = [
    # ---- The standard validators: each one's rule, against its failing fixture ----
    {
        "name": "requirement_support: a link to an item the target does not hold resolves",
        "edits": [(REQUIREMENTS, "else if (!targets.Any(t => t.Value.Items.Any(x => x.Id == link.Requirement)))",
                   "else if (targets.Count < 0)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_support: coverage is never checked",
        "edits": [(REQUIREMENTS, 'if (!c.Option("require_coverage", true) ||', 'if (plans.Count >= 0 || !c.Option("require_coverage", true) ||')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_ownership: an item with no owner is only a warning",
        "edits": [(REQUIREMENTS, 'c.Error("missing_owner"', 'c.Warn("missing_owner"')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_inheritance: a dropped inherited item is only a warning",
        "edits": [(REQUIREMENTS, 'c.Error("missing_inherited"', 'c.Warn("missing_inherited"')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_inheritance: rewording is never refused",
        "edits": [(REQUIREMENTS, "else if (!rewording && (", "else if (rewording && !rewording && (")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_inheritance: an absent parent plan is not a pass (LOG 63)",
        "edits": [(REQUIREMENTS, "        if (parents.Count == 0)\n            return;\n        if (own.Count == 0)", "        if (own.Count == 0)")],
        "tests": [STD_TESTS, ENGINE],
        "expect": [FIXTURES, "A_standalone_tickets_plan_is_checked_by_the_standard_validators_against_the_frozen_head"],
    },
    {
        "name": "verification_method_support: a criterion with no method and no owner is only a warning",
        "edits": [(REQUIREMENTS, 'c.Error("no_method"', 'c.Warn("no_method"')],
        "tests": [STD_TESTS, ENGINE],
        "expect": [FIXTURES, "An_invalid_plan_is_invalid_and_its_report_is_kept_for_the_planner"],
    },
    {
        "name": "verification_satisfaction: a claimed test passes whatever the engine saw",
        "edits": [(EVIDENCE, "        if (tiers.Any(t => t.Results.Failures.Any(f => f.Name == id)))\n            return \"it failed\";",
                   "        if (id.Length >= 0)\n            return null;\n        if (tiers.Any(t => t.Results.Failures.Any(f => f.Name == id)))\n            return \"it failed\";")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES, "Satisfaction_passes_with_neither_plan_nor_claims_as_for_an_epic_and_refuses_a_tier_judged_by_exit_code"],
    },
    {
        "name": "verification_satisfaction: results at an earlier revision than the frozen head count",
        "edits": [(EVIDENCE, "Check.SameRepository(o.Repository, repository) && o.Revision == revision).ToList();",
                   "Check.SameRepository(o.Repository, repository)).ToList();")],
        "tests": [STD_TESTS],
        "expect": ["Satisfaction_checks_the_frozen_head_not_an_earlier_passing_run"],
    },
    {
        "name": "verification_satisfaction: a child's test results in the index count as this run's",
        "edits": [(EVIDENCE, "c.EngineEvidence(Types.TestResults, Check.TestActions).Where(e => own.Contains(e.RunId))", "c.EngineEvidence(Types.TestResults, Check.TestActions)")],
        "tests": [STD_TESTS],
        "expect": ["Satisfaction_counts_only_this_runs_test_results_not_a_childs"],
    },
    {
        "name": "F7: test results an agent or a non-testing action produced count as evidence",
        "edits": [(PROVIDER, "a.Evidence && a.Action is { } action && actions.Contains(action) && ValidatorDescriptor.TypeName(a.Type) == type",
                   "ValidatorDescriptor.TypeName(a.Type) == type")],
        "tests": [STD_TESTS, ENGINE],
        "expect": [FIXTURES, ("Test_results_not_made_by_an_engine_test_action_prove_nothing_to_the_trace", 3),
                   ("Only_test_results_an_engine_test_action_produced_satisfy_a_claim", 2)],
    },
    {
        "name": "requirement_trace: a merge record does not carry a squash-merged head into the work",
        "edits": [(EVIDENCE, "foreach (var merge in tree.Merges.Where(", "foreach (var merge in tree.Merges.Take(0).Where(")],
        "tests": [STD_TESTS],
        "expect": ["The_trace_follows_an_owner_into_a_child_and_a_squash_merge_into_the_work"],
    },
    {
        "name": "requirement_trace: any revision counts as contained in the work",
        "edits": [(EVIDENCE, "if (await c.ContainsAsync(repository, head, revision))", "if (head.Length >= 0 || await c.ContainsAsync(repository, head, revision))")],
        "tests": [STD_TESTS],
        "expect": ["The_trace_has_a_gap_when_the_tested_head_never_landed_in_the_work"],
    },
    {
        "name": "requirement_trace: an owner is not followed into its child",
        "edits": [(EVIDENCE, "if (item.Owner is { } owner && !Owns(owner, plan.Document.Scope))", "if (item.Id is null && item.Owner is { } owner && !Owns(owner, plan.Document.Scope))")],
        "tests": [STD_TESTS],
        "expect": ["The_trace_follows_an_owner_into_a_child_and_a_squash_merge_into_the_work"],
    },
    {
        "name": "tests_added: a claimed test file unchanged since the finding was raised counts",
        "edits": [(EVIDENCE, "if (await c.FileAsync(repository, file, raised) is { } before && before.Hash == after.Hash)",
                   "if (file.Length < 0 && await c.FileAsync(repository, file, raised) is { } before && before.Hash == after.Hash)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES, "Tests_added_catches_a_fix_that_adds_no_test_in_a_real_git_history"],
    },
    {
        "name": "tests_added: a changed test file that does not hold the claimed test counts",
        "edits": [(EVIDENCE, "if (!after.Content.Contains(symbol, StringComparison.Ordinal))",
                   "if (file.Length < 0 && !after.Content.Contains(symbol, StringComparison.Ordinal))")],
        "tests": [STD_TESTS],
        "expect": ["Tests_added_catches_a_fix_that_adds_no_test_in_a_real_git_history"],
    },
    {
        "name": "tests_added: a finding whose regression test does not apply still needs one",
        "edits": [(EVIDENCE, "e.State == FindingStates.Open && e.RegressionTest?.Applicable != false", "e.State == FindingStates.Open")],
        "tests": [STD_TESTS],
        "expect": ["Tests_added_asks_nothing_of_a_finding_where_a_regression_test_does_not_apply_and_names_a_claim_without_a_source"],
    },
    # ---- Added for the review of PR 13 ----
    {
        "name": "review F1: any owner excuses a criterion (the run itself, or a child that never planned it)",
        "edits": [(PROVIDER, "item.Owner is { Kind: { } kind, Id.Length: > 0 } && scope is not null && kind == ChildKind(scope.Kind);",
                   "item.Owner is not null;"),
                  warn(REQUIREMENTS, "owner_in_child"),
                  (EVIDENCE, "                if (Check.Delegated(criterion, plan.Value.Scope))\n",
                   "                if (criterion.Owner is not null)\n                    continue;\n                if (Check.Delegated(criterion, plan.Value.Scope))\n")],
        "tests": [STD_TESTS, it("ForgeTicketTests.A_planner_that_marks")],
        "expect": [FIXTURES, "A_planner_that_marks_its_criteria_as_owned_to_skip_testing_does_not_get_a_merge"],
    },
    {
        "name": "review F1: a delegated criterion is excused whether or not its owner planned it",
        "edits": [(EVIDENCE, "if (tree.Plans.Any(p => !own.Contains(p.RunId) && Owns(criterion.Owner!, p.Document.Scope) && p.Document.Items.Any(i => i.Id == criterion.Id)))",
                   "if (tree.Plans.Count >= 0)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F1: an unproved criterion (no method) is only a warning",
        "edits": [warn(EVIDENCE, "unproved")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F2: tests_added does not need the claimed test to have passed",
        "edits": [(EVIDENCE, "if (NotPassed(test.Id!, observed.Where(", "if (test.Id!.Length < 0 && NotPassed(test.Id!, observed.Where(")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES, "Tests_added_catches_a_fix_that_adds_no_test_in_a_real_git_history"],
    },
    {
        "name": "review F2: tests_added accepts the production file the finding is about",
        "edits": [(EVIDENCE, "if (file == Path(finding.Finding.Location.File))", "if (file == Path(finding.Finding.Location.File) + \"x\")")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F2: tests_added takes the claimed test's own symbol (free text)",
        "edits": [(f"{STD}/Documents.cs", "public string? Symbol => Id?.Split", "public string? Symbol => Source?.Symbol ?? Id?.Split")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: tests_added ignores the claim's repository",
        "edits": [(EVIDENCE, "tests.Where(t => t.Repository is not null && Check.SameRepository(t.Repository, repository)))", "tests.Where(t => t.Repository is not null))")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {"name": "review F4: no_raised_revision is only a warning", "edits": [warn(EVIDENCE, "no_raised_revision")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: claim_mismatch is only a warning", "edits": [warn(EVIDENCE, "claim_mismatch")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: change_not_allowed is only a warning", "edits": [warn(EVIDENCE, "change_not_allowed")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: unknown_claim is only a warning", "edits": [warn(EVIDENCE, "unknown_claim")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: invalid_owner is only a warning", "edits": [warn(REQUIREMENTS, "invalid_owner")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: owned_by_another is only a warning", "edits": [warn(REQUIREMENTS, "owned_by_another")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: links_changed is only a warning", "edits": [warn(REQUIREMENTS, "links_changed")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: support_and_reason is only a warning", "edits": [warn(REQUIREMENTS, "support_and_reason")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {"name": "review F4: owned_with_methods is only a warning", "edits": [warn(REQUIREMENTS, "owned_with_methods")], "tests": [STD_TESTS], "expect": [FIXTURES]},
    {
        "name": "review F4: a missing scope is only a warning",
        "edits": [(PROVIDER, 'Error("malformed", $"{Describe(subject)} has no scope kind and id"', 'Warn("malformed", $"{Describe(subject)} has no scope kind and id"')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F7: an empty plan is accepted",
        "edits": [(PROVIDER, 'Error("malformed", $"{Describe(subject)} holds no requirements or acceptance criteria"', 'Warn("malformed", $"{Describe(subject)} holds no requirements or acceptance criteria"')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: a repeated id is only a warning",
        "edits": [(PROVIDER, 'Error("duplicate_id", $"{Describe(subject)} uses id', 'Warn("duplicate_id", $"{Describe(subject)} uses id')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: baseline test results count as the change's",
        "edits": [(EVIDENCE, "is not { Baseline: false } evidence)", "is not { } evidence)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: a test both passed and failed counts as passed",
        "edits": [(EVIDENCE, "        if (tiers.Any(t => t.Results.Failures.Any(f => f.Name == id)))\n            return \"it failed\";\n        if (tiers.Any(t => t.Results.Passing?.Contains(id) == true))\n            return null;",
                   "        if (tiers.Any(t => t.Results.Passing?.Contains(id) == true))\n            return null;\n        if (tiers.Any(t => t.Results.Failures.Any(f => f.Name == id)))\n            return \"it failed\";")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: the method's tier is ignored (any tier at the revision)",
        "edits": [(EVIDENCE, "return named.Count > 0 ? named : there;", "return there;")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "review F4: the trace takes any run's tests",
        "edits": [(EVIDENCE, "var runs = tree.Observed.Where(o => o.RunId == plan.RunId);", "var runs = tree.Observed.Where(o => o.RunId.Length >= 0);")],
        "tests": [STD_TESTS],
        "expect": [("The_trace_takes_tests_and_claims_only_from_the_run_whose_plan_holds_the_item", 1)],
    },
    {
        "name": "review F4: the trace takes any run's claims",
        "edits": [(EVIDENCE, "var claims = tree.Claims.GetValueOrDefault(plan.RunId) ?? [];", "var claims = tree.Claims.Values.SelectMany(x => x).ToList();")],
        "tests": [STD_TESTS],
        "expect": [("The_trace_takes_tests_and_claims_only_from_the_run_whose_plan_holds_the_item", 1)],
    },
    {
        "name": "review F4: with no VCS provider every revision counts as contained",
        "edits": [(PROVIDER, "        if (vcs is null)\n            return false;", "        if (vcs is null)\n            return true;")],
        "tests": [STD_TESTS],
        "expect": ["Tests_at_an_ancestor_of_the_head_are_contained_through_the_VCS_and_not_without_it"],
    },
    {
        "name": "review F3: a subject is the producer with the last step id, not the one an agent step would consume",
        "edits": [(VALIDATE, ".ThenBy(a => a.StepId, StringComparer.Ordinal)", ".ThenByDescending(a => a.StepId, StringComparer.Ordinal)")],
        "tests": [ENGINE],
        "expect": ["A_subject_with_several_producers_is_the_one_an_agent_step_beside_it_would_consume"],
    },
    {
        "name": "review F5: a set is never expanded",
        "edits": [(VALIDATE, 'if (record?.Action != "artifact.compose")', 'if (record?.Action != "artifact.compose" || reference.Length >= 0)')],
        "tests": [ENGINE],
        "expect": ["A_set_made_by_artifact_compose_is_passed_with_its_members"],
    },
    {
        "name": "review F6: a validator's infrastructure error stops for a person instead of being retried",
        "edits": [(VALIDATE, "if (error.Class == ProviderErrorClass.Infrastructure && !error.IsConfigurationFault)", "if (error.Class == ProviderErrorClass.Infrastructure && error.IsConfigurationFault)")],
        "tests": [ENGINE],
        "expect": ["A_validators_infrastructure_error_is_retried_not_an_intervention"],
    },
    {
        "name": "review F4: a map-form subject is optional unless it says otherwise",
        "edits": [(VALIDATE, 's.GetValueOrDefault("optional") as bool? ?? false', 's.GetValueOrDefault("optional") as bool? ?? true')],
        "tests": [ENGINE],
        "expect": ["A_required_subject_that_does_not_exist_or_cannot_be_read_stops_the_step_for_a_person"],
    },
    # ---- Added for the re-check of PR 13 ----
    {
        "name": "re-check N1: record_fixes goes straight to tests_added, leaving retest unreachable",
        "edits": [(FIX_LOOP, "        - to: retest\n", "        - to: tests_added\n")],
        "tests": [STD_WORKFLOWS],
        "expect": [("Std_workflow_test_passes", 2)],
    },
    {
        "name": "re-check N3: a test claimed as new that already passed where the finding was raised counts",
        "edits": [(EVIDENCE, 'if (test.Change == "new" && NotPassed(', 'if (test.Change == "never" && NotPassed(')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N3: the finding's file is compared without normalising its path",
        "edits": [(EVIDENCE, "if (file == Path(finding.Finding.Location.File))", "if (test.Source!.File == finding.Finding.Location.File)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N4: any owner kind is delegated",
        "edits": [(PROVIDER, "&& scope is not null && kind == ChildKind(scope.Kind);", "&& scope is not null && kind.Length >= 0;")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N4: the owner's plan may be in this run",
        "edits": [(EVIDENCE, "tree.Plans.Any(p => !own.Contains(p.RunId) && Owns(", "tree.Plans.Any(p => Owns(")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N4: the owner's plan need not hold the criterion",
        "edits": [(EVIDENCE, "&& Owns(criterion.Owner!, p.Document.Scope) && p.Document.Items.Any(i => i.Id == criterion.Id)))", "&& Owns(criterion.Owner!, p.Document.Scope)))")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N4: tests_added counts a test that passed at any revision of this run",
        "edits": [(EVIDENCE, "observed.Where(o => Check.SameRepository(o.Repository, repository) && o.Revision == head).ToList()) is { } failed)",
                   "observed.Where(o => Check.SameRepository(o.Repository, repository)).ToList()) is { } failed)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "re-check N4: subjects from several runs are accepted",
        "edits": [(EVIDENCE, "        if (own.Count != 1)\n", "        if (own.Count == 0)\n")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    # ---- Evidence the validators read ----
    {
        "name": "test results record no passing test identities (TRX)",
        "edits": [(RESULTS, "                    passed++;\n                    passing.Add(name);", "                    passed++;")],
        "tests": [ENGINE],
        "expect": ["TRX_counts_outcomes_and_keeps_each_failures_first_line"],
    },
    {
        "name": "test results record no passing test identities (JUnit)",
        "edits": [(RESULTS, "                passed++;\n                passing.Add(name);", "                passed++;")],
        "tests": [ENGINE],
        "expect": ["JUnit_names_tests_by_class_and_name_and_counts_errors_as_failures", "Several_files_of_a_tier_are_summed"],
    },
    {
        "name": "an expression's maps stay CEL's own, so std's level_validators reach artifact.validate as pairs",
        "edits": [(CEL, "System.Collections.IDictionary map => map.Keys", "System.Collections.IDictionary map when map.Count < 0 => map.Keys")],
        "tests": [EXPRESSIONS],
        "expect": ["Lists_and_maps_come_back_as_plain_values_all_the_way_down"],
    },
    # ---- artifact.validate ----
    {
        "name": "artifact.validate: an optional subject that does not exist stops the step",
        "edits": [(VALIDATE, "            return spec.Optional\n", "            return spec.Name.Length < 0\n")],
        "tests": [ENGINE],
        "expect": ["A_standalone_tickets_plan_is_checked_by_the_standard_validators_against_the_frozen_head",
                   "A_validator_is_found_in_any_validator_provider_and_gets_the_run_trees_index_and_ledger_and_subjects_from_other_runs"],
    },
    {
        "name": "artifact.validate: a required subject that does not exist is passed as absent",
        "edits": [(VALIDATE, "            return spec.Optional\n", "            return spec.Name.Length >= 0\n")],
        "tests": [ENGINE],
        "expect": ["A_required_subject_that_does_not_exist_or_cannot_be_read_stops_the_step_for_a_person"],
    },
    {
        "name": "artifact.validate: an unresolved subject from a parent run passes as absent",
        "edits": [(VALIDATE, 'spec.From != "current" && hasParent)', 'spec.From != "current" && hasParent && spec.Name.Length < 0)')],
        "tests": [ENGINE],
        "expect": ["In_a_child_run_a_subject_from_its_parent_the_workflow_did_not_resolve_stops_the_step_rather_than_passing_as_absent"],
    },
    {
        "name": "artifact.validate: the work branch's name is frozen, not its head",
        "edits": [(VALIDATE, "                frozen[name] = head;", "                frozen[name] = run.Scope.WorkBranch;")],
        "tests": [ENGINE],
        "expect": ["A_standalone_tickets_plan_is_checked_by_the_standard_validators_against_the_frozen_head",
                   "The_trace_report_is_kept_as_trace_report_and_the_validation_report_as_the_other_artifact"],
    },
    {
        "name": "artifact.validate: descendants' artifacts are left out of the index",
        "edits": [(VALIDATE, "index.AddRange((await Of(runId)).Where(a => a.RunId != runId));", "index.AddRange((await Of(runId)).Where(a => a.RunId == \"\"));")],
        "tests": [ENGINE],
        "expect": ["A_validator_is_found_in_any_validator_provider_and_gets_the_run_trees_index_and_ledger_and_subjects_from_other_runs"],
    },
    {
        "name": "artifact.validate: an offered trace report is not kept as trace_report",
        "edits": [(VALIDATE, "artifacts[promised] = offered.LastOrDefault(r => ValidatorDescriptor.TypeName(r.Type) == promised)?.Artifact.Id ?? reference;",
                   "artifacts[promised] = reference;")],
        "tests": [ENGINE],
        "expect": ["The_trace_report_is_kept_as_trace_report_and_the_validation_report_as_the_other_artifact"],
    },
    {
        "name": "artifact.validate: only the default validator provider is searched",
        "edits": [(VALIDATE, "foreach (var (name, instance) in providers.Instances(\"validator\"))", "foreach (var (name, instance) in providers.Instances(\"validator\").Take(1))")],
        "tests": [ENGINE],
        "expect": ["A_validator_is_found_in_any_validator_provider_and_gets_the_run_trees_index_and_ledger_and_subjects_from_other_runs"],
    },
    {
        "name": "artifact.validate is not dispatched to the validators",
        "edits": [(ACTIONS, '                "artifact.validate" => await Validating.ValidateAsync(run, Providers),\n', "")],
        "tests": [ENGINE],
        "expect": [("Only_test_results_an_engine_test_action_produced_satisfy_a_claim", 3), "An_invalid_plan_is_invalid_and_its_report_is_kept_for_the_planner"],
    },
    # ---- F7 at load (check 12, from M1; M6's verified-by) ----
    {
        "name": "check 12: an agent step declaring an evidence type loads",
        "edits": [(VALIDATOR, "if (step.Form is StepForm.Agent or StepForm.Decision && ArtifactType(typeRef)?.Evidence == true)",
                   "if (step.Form is StepForm.Agent or StepForm.Decision && ArtifactType(typeRef)?.Evidence == true && step.Id.Length < 0)")],
        "tests": [VALIDATION_TESTS],
        "expect": ["Mutation_triggers_its_check_and_no_other"],
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
