"""Control runs for the M2 checks ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the
source, and runs the same tests again. A control holds when its named tests fail while the rule is broken and
every test passes after the revert.

Usage, from anywhere:  python3 tools/check_m2_controls.py
Exit status 1 if any control does not hold. Takes a few minutes.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = "src/FeatureFlow.Interpreter/RunCore.cs"
VALUES = "src/FeatureFlow.Interpreter/Values.cs"
REDRIVE = "src/FeatureFlow.Testing.Workflows/FeatureFlow.Testing.Workflows.csproj"

CONTROLS = [
    {
        "name": "allowance check removed (F4)",
        "edits": [(CORE, "if (record.AttemptsUsed >= Allowance(step))", "if (false && record.AttemptsUsed >= Allowance(step))")],
        "tests": ["tests/FeatureFlow.Testing.Workflows.Tests", "tests/FeatureFlow.Interpreter.Tests"],
        "expect": ['Std_workflow_test_passes(name: "ticket_fix_exhausted")', 'Std_workflow_test_passes(name: "epic_fix_exhausted")',
                   'Std_workflow_test_passes(name: "project_plan_exhausted")',
                   "Every_entry_counts_against_the_allowance_and_the_entry_after_the_last_is_refused"],
    },
    {
        "name": "routing without progress not detected (review B1)",
        "edits": [(CORE, "if (!enteredWithoutProgress.Add(step.Id))", "if (false && !enteredWithoutProgress.Add(step.Id))")],
        "tests": ["tests/FeatureFlow.Interpreter.Tests"],
        "expect": ["An_exhausted_step_routed_back_into_itself_escalates_instead_of_looping",
                   "Reaching_the_run_ceiling_in_a_self_loop_escalates_instead_of_looping"],
    },
    {
        "name": "race allowance removed (LOG 61)",
        "edits": [(CORE, "if (IsMerge(step) && record.RacesUsed >= RaceAllowance(step))", "if (false && record.RacesUsed >= RaceAllowance(step))")],
        "tests": ["tests/FeatureFlow.Testing.Workflows.Tests"],
        "expect": ['Std_workflow_test_passes(name: "ticket_races_exhausted")'],
    },
    {
        "name": "end-route rule removed (F9): a run with nothing left reports completed",
        "edits": [(CORE, "End(RunStatus.Incomplete, \"\", \"nothing is ready", "End(RunStatus.Completed, \"\", \"nothing is ready")],
        "tests": ["tests/FeatureFlow.Interpreter.Tests", "tests/FeatureFlow.Testing.Workflows.Tests"],
        "expect": ["An_unrouted_failure_ends_the_run_incomplete_not_completed"],
    },
    {
        "name": "re-entry keeps the current execution (DR12)",
        "edits": [(CORE, "            record.Previous = record.Current;\n            record.Executions++;\n            record.Current = new Execution { Number = record.Executions };\n            if (!IsMerge(step))",
                   "            record.Executions++;\n            record.Current ??= new Execution { Number = record.Executions };\n            if (!IsMerge(step))")],
        "tests": ["tests/FeatureFlow.Interpreter.Tests", "tests/FeatureFlow.Testing.Workflows.Tests"],
        "expect": ["Reentry_clears_the_current_execution_and_keeps_the_previous_readable",
                   'Std_workflow_test_passes(name: "ticket_stale_output")'],
    },
    {
        "name": "a chain goes on after an item fails",
        "edits": [(CORE, "            if (cls != OutcomeClass.Success)\n                Join(container, state, \"any_failed\");\n            else if (open == 0)",
                   "            if (cls == (OutcomeClass)99)\n                Join(container, state, \"any_failed\");\n            else if (open == 0)"),
                  (CORE, "if (step.Form == StepForm.Chain && state.Branches.Take(i).Any(b => b != BranchStatus.Succeeded))",
                   "if (false && state.Branches.Take(i).Any(b => b != BranchStatus.Succeeded))")],
        "tests": ["tests/FeatureFlow.Testing.Workflows.Tests", "tests/FeatureFlow.Interpreter.Tests"],
        "expect": ['Std_workflow_test_passes(name: "project_partial_delivery")'],
    },
    {
        "name": "join all ignores a failed branch",
        "edits": [(CORE, "Join(container, state, succeeded == state.Branches.Count ? \"completed\" : \"any_failed\");",
                   "Join(container, state, \"completed\");")],
        "tests": ["tests/FeatureFlow.Testing.Workflows.Tests"],
        "expect": ['Std_workflow_test_passes(name: "epic_child_failed")'],
    },
    {
        "name": "no CleanUp command emitted",
        "edits": [(CORE, "        outbox.Add(new CleanUp(step.Id));\n", "")],
        "tests": ["tests/FeatureFlow.Testing.Workflows.Tests"],
        "expect": ['Std_workflow_test_passes(name: "ticket_spec_rejected")'],
    },
    {
        "name": "number outputs not converted to whole numbers (LOG 76)",
        "edits": [(VALUES, "    public static (object? Value, string? Error) Convert(object? value, string? type)\n    {\n",
                   "    public static (object? Value, string? Error) Convert(object? value, string? type)\n    {\n        if (type is \"number\" or null) return (value, null);\n")],
        "tests": ["tests/FeatureFlow.Interpreter.Tests"],
        "expect": ["A_loop_guard_reads_the_new_execution_and_a_number_given_as_a_double_evaluates_as_checked",
                   "An_undeclared_output_and_a_fractional_number_are_refused"],
    },
    {
        "name": "Temporalio referenced from the re-drive runner (LOG 66)",
        "edits": [(REDRIVE, "  <ItemGroup>\n", "  <ItemGroup>\n    <PackageReference Include=\"Temporalio\" />\n")],
        "tests": ["tests/FeatureFlow.Architecture.Tests"],
        "expect": ["Every_project_under_src_and_hosts_obeys_the_reference_rules"],
    },
]


def run_tests(projects):
    failing = set()
    output = []
    for project in projects:
        run = subprocess.run(["dotnet", "test", project], cwd=ROOT, capture_output=True, text=True)
        output.append(run.stdout)
        failing.update(re.findall(r"^\s+Failed [\w.]+\.(\w+(?:\(name: \"\w+\"\))?) \[", run.stdout, re.M))
        if run.returncode != 0 and not failing:
            failing.add("(build or run failed)")
    return failing, "\n".join(output)


def main():
    ok = True
    for control in CONTROLS:
        originals = {}
        for path, old, new in control["edits"]:
            full = os.path.join(ROOT, path)
            text = originals.get(full) or open(full).read()
            originals.setdefault(full, text)
            current = open(full).read()
            if current.count(old) != 1:
                print(f"{control['name']}: the text to replace occurs {current.count(old)} times in {path}", flush=True)
                sys.exit(1)
            open(full, "w").write(current.replace(old, new))
        try:
            broken, _ = run_tests(control["tests"])
        finally:
            for full, text in originals.items():
                open(full, "w").write(text)
        reverted, _ = run_tests(control["tests"])
        missing = [t for t in control["expect"] if t not in broken]
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
