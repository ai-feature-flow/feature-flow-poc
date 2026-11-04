"""Control runs for M6's integration (W5) ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the source,
and runs the same tests again. A control holds when its named tests fail while the rule is broken and every test
passes after the revert. A name matches a failing test when the failing test's name starts with it.

Integration tests need Temporal at FF_TEMPORAL_ADDRESS, and on this machine
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=safe.bareRepository GIT_CONFIG_VALUE_0=all.

Usage, from anywhere:  python3 tools/check_m6_integration_controls.py [name ...]
Exit status 1 if any control does not hold.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FINDINGS = "src/FeatureFlow.Engine/Actions/Findings.cs"
FIX_LOOP = "library/std/fragments/fix_loop.yaml"
VERIFY = "library/std/fragments/verify_and_review.yaml"
STD_VALIDATORS = "src/Providers/FeatureFlow.Providers.StdValidators/StdValidators.cs"
REQUIREMENTS = "src/Providers/FeatureFlow.Providers.StdValidators/Requirements.cs"
STD_TESTS = ("tests/Providers/FeatureFlow.Providers.StdValidators.Tests", None)
FIXTURES = "Every_fixture_gets_its_verdict_and_the_verdict_agrees_with_the_issues"
ENGINE_FINDINGS = ("tests/FeatureFlow.Engine.Tests", "FullyQualifiedName~FindingsActionTests")
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
SCOPE = "src/FeatureFlow.Engine/Actions/ActionScope.cs"
VALIDATE = "src/FeatureFlow.Engine/Actions/Validation.cs"
ENGINE_VALIDATION = ("tests/FeatureFlow.Engine.Tests", "FullyQualifiedName~ValidationActionTests")
FORGE = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~ForgeTicketTests.A_std_ticket_runs_on_the_memory_providers")
PARENT_REF = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~The_checkout_holds_the_parent_branch")
CHILD_VALIDATE = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~A_childs_artifact_validate")
AGENT = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"
SIBLING = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~A_sibling_merging")
BRANCHING = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~A_child_in_a_repository")
LOOP = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~FindingsLoopTests.After_the_fix_loop")

CONTROLS = [
    # ---- Review of PR 14 ----
    {
        "name": "F1: a subject from another run the step does not consume passes as absent",
        "edits": [(VALIDATE, 'if (run.Scope.Consumed is not null && spec.From != "current" && run.Scope.Declared?.Contains(key) != true)',
                   'if (run.Scope.Consumed is not null && spec.From != "current" && run.Scope.Declared?.Contains(key) != true && key == "")')],
        "tests": [ENGINE_VALIDATION],
        "expect": ["A_subject_from_another_run_the_step_does_not_consume_stops_the_step_rather_than_passing_as_absent"],
    },
    {
        "name": "F1: std's plan check stops consuming the parent's child_requirements (the reviewer's mutation)",
        "edits": [("library/std/fragments/plan_and_validate.yaml", "      - { name: child_requirements, from: parent, optional: true }\n      - { name: requirements, from: parent, optional: true }\n    produces:",
                   "      - { name: requirements, from: parent, optional: true }\n    produces:")],
        "tests": [FORGE],
        "expect": ["A_std_ticket_runs_on_the_memory_providers_to_an_engine_merge_into_its_parent_branch"],
    },
    {
        "name": "F2: the parent's links compared as written, not as the child sees them",
        "edits": [(REQUIREMENTS, '_ when byParent && (artifact.StartsWith("parent/", StringComparison.Ordinal) || artifact.StartsWith("root/", StringComparison.Ordinal)) =>',
                   '_ when byParent && artifact.StartsWith("never/", StringComparison.Ordinal) =>')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "F2: a word-for-word copy of a link is not accepted as kept",
        "edits": [(REQUIREMENTS, "&& (k.Artifact == l.Artifact || Target(k.Artifact, false) == Target(l.Artifact, true))", "&& Target(k.Artifact, false) == Target(l.Artifact, true)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "F2: an item given to a child may link upward",
        "edits": [(REQUIREMENTS, "else if ((item.Supports ?? []).FirstOrDefault(l => l.Artifact?.Contains('/') == true) is { } upward)",
                   "else if ((item.Supports ?? []).FirstOrDefault(l => l.Artifact?.Contains('|') == true) is { } upward)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "F3: the agent's checkout lacks the parent branch",
        "edits": [(AGENT, "if (parent != workBranch)", "if (parent != workBranch && parent == \"\")")],
        "tests": [PARENT_REF],
        "expect": ["The_checkout_holds_the_parent_branch_at_its_head"],
    },
    {
        "name": "F4: a name several repositories end with is taken as the first",
        "edits": [(SCOPE, "return matches.Count == 1 ? matches[0] : written;", "return matches.Count >= 1 ? matches[0] : written;")],
        "tests": [ENGINE_FINDINGS],
        "expect": ["A_repository_is_named_as_the_run_names_it_only_when_exactly_one_matches"],
    },
    {
        "name": "F5: a subject of this run the step does not consume is not found",
        "edits": [(VALIDATE, "? consumed.GetValueOrDefault(key) ?? (spec.From == \"current\" ? Latest(run, spec.Name) : null)", "? consumed.GetValueOrDefault(key)")],
        "tests": [ENGINE_VALIDATION],
        "expect": ["A_subject_of_this_run_the_step_does_not_consume_is_its_latest_of_the_name"],
    },
    {
        "name": "std's project delivers heads it never tested (LOG 197, extended)",
        "edits": [("library/std/templates/project_delivery.yaml", "    routes:\n      passed: tests\n", "    routes:\n      passed: trace\n")],
        "tests": [("tests/FeatureFlow.Testing.Workflows.Tests", "FullyQualifiedName~Std_workflow_test_passes")],
        "expect": ["Std_workflow_test_passes"],
    },
    {
        "name": "inheritance refuses a copy that adds a link (LOG 191, amended)",
        "edits": [(REQUIREMENTS, "else if (Links(item).Except(Links(copy, parent.Subject)).Any())", "else if (!Links(copy, parent.Subject).SequenceEqual(Links(item)))")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "inheritance lets a copy drop a parent's link (LOG 191, amended)",
        "edits": [(REQUIREMENTS, "else if (Links(item).Except(Links(copy, parent.Subject)).Any())", "else if (Links(item).Except(Links(copy, parent.Subject)).Any() && item.Id == null)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "requirement_support checks the parent's own links (LOG 199)",
        "edits": [(REQUIREMENTS, 'if (plan.Subject.From != "current")\n                continue;', 'if (plan.Subject.From == "never")\n                continue;')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "run.child passes a child's repositories as written (LOG 198)",
        "edits": [(SCOPE, "return matches.Count == 1 ? matches[0] : written;", "return written;")],
        "tests": [BRANCHING],
        "expect": ["A_child_in_a_repository_its_parent_never_touched_finds_its_parent_branch_there"],
    },
    {
        "name": "a parent starts a child without making its work branch in the child's repositories (LOG 196)",
        "edits": [(WORKFLOW, "if (Workflow.Patched(ParentBranchesPatch))", "if (Workflow.Patched(ParentBranchesPatch) && repositories.Count < 0)")],
        "tests": [BRANCHING],
        "expect": ["A_child_in_a_repository_its_parent_never_touched_finds_its_parent_branch_there"],
    },
    {
        "name": "the agent step's baseline is the parent branch's head (LOG 193)",
        "edits": [(AGENT, "await vcs.MergeBaseAsync(repo, branchRevision, parentHead, ct)", "await vcs.MergeBaseAsync(repo, parentHead, parentHead, ct)")],
        "tests": [SIBLING],
        "expect": ["A_sibling_merging_into_the_parent_branch_is_not_part_of_the_change_the_reviewers_read"],
    },
    {
        "name": "the workflow gives an action no consumed artifacts (LOG 191)",
        "edits": [(WORKFLOW, "Consumed: Workflow.Patched(ChildInputsPatch) ? consumed : null,", "Consumed: null,")],
        "tests": [CHILD_VALIDATE],
        "expect": ["A_childs_artifact_validate_reads_its_parents_requirements_and_a_parent_link_resolves"],
    },
    {
        "name": "end to end: a link names a parent's document only by its bare name (LOG 191)",
        "edits": [(STD_VALIDATORS, '(s.From != "current" && artifact == $"{s.From.Replace(\':\', \'/\')}/{s.Name}")', "false")],
        "tests": [CHILD_VALIDATE],
        "expect": ["A_childs_artifact_validate_reads_its_parents_requirements_and_a_parent_link_resolves"],
    },
    {
        "name": "a link names a parent's document only by its bare name (LOG 191)",
        "edits": [(STD_VALIDATORS, '(s.From != "current" && artifact == $"{s.From.Replace(\':\', \'/\')}/{s.Name}")', "false")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "a parent/ link resolves to any document of the name, this run's included (LOG 191)",
        "edits": [(STD_VALIDATORS, '(s.From != "current" && artifact == $"{s.From.Replace(\':\', \'/\')}/{s.Name}")',
                   '(artifact != null && artifact.EndsWith("/" + s.Name))')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "inheritance compares a copy's parent/ links literally (LOG 191)",
        "edits": [(REQUIREMENTS, "Links(copy, parent.Subject)", "Links(copy)")],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "inheritance strips any source prefix from a copy's links (LOG 191)",
        "edits": [(REQUIREMENTS, 'var prefix = from is null || from.From == "current" ? null : from.From.Replace(\':\', \'/\') + "/";',
                   'var prefix = from is null || from.From == "current" ? null : "root/";')],
        "tests": [STD_TESTS],
        "expect": [FIXTURES],
    },
    {
        "name": "findings.record keeps a reviewer's short repository name (LOG 190)",
        "edits": [(SCOPE, "return matches.Count == 1 ? matches[0] : written;", "return written;")],
        "tests": [ENGINE_FINDINGS],
        "expect": ["A_finding_naming_the_runs_repository_by_short_name_or_case_is_recorded_in_it_and_raised_at_its_head"],
    },
    {
        "name": "findings.triage lets a finding with no raised revision be fixed (LOG 190)",
        "edits": [(FINDINGS, "problems.Add($\"{t.Finding}: it was raised in", "_ = ($\"{t.Finding}: it was raised in")],
        "tests": [ENGINE_FINDINGS],
        "expect": ["A_finding_in_a_repository_the_run_has_not_prepared_cannot_be_fixed_by_a_triage"],
    },
    {
        "name": "the merge bar ignores open blocking findings after the fix loop (seam a)",
        "edits": [(VERIFY, 'when: "findings.open_blocking == 0 && params.check_satisfaction"', 'when: "params.check_satisfaction"')],
        "tests": [LOOP],
        "expect": ["After_the_fix_loop_the_merge_bar_decides_on_what_is_still_open"],
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
