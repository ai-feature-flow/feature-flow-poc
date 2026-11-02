"""Control runs for the M3 checks ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the
source, and runs the same tests again. A control holds when its named tests fail while the rule is broken and
every test passes after the revert.

The integration controls need a Temporal dev server; FF_TEMPORAL_ADDRESS names it (default localhost:7233).
Run in the working tree with no clone inside it: a stray clone breaks the layout tests.

Usage, from anywhere:  FF_TEMPORAL_ADDRESS=localhost:7833 python3 tools/check_m3_controls.py [name ...]
Exit status 1 if any control does not hold. Takes about half an hour; the continue-as-new controls each wait
for the scenario's three-minute deadline.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
CARRY = "src/FeatureFlow.Interpreter/Carry.cs"
CHECKS = "src/FeatureFlow.Validation/RunStartChecks.cs"
API_PROJECT = "src/FeatureFlow.Api/FeatureFlow.Api.csproj"

INTEGRATION = "tests/FeatureFlow.Integration.Tests"
SP1 = (INTEGRATION, "FullyQualifiedName~ContinueAsNewTests")
SP1_TEST = "Children_ending_and_answers_arriving_during_hand_overs_are_not_lost"

CONTROLS = [
    {
        "name": "n1: a child signals its parent's run id, not its workflow id (SP1 N1)",
        "edits": [(WORKFLOW, "await Workflow.GetExternalWorkflowHandle(workflowId).SignalAsync(signal, args);",
                   "await Workflow.GetExternalWorkflowHandle(workflowId, signal == RunWorkflowNames.ChildEnded ? Workflow.Info.Parent?.RunId : null).SignalAsync(signal, args);")],
        "tests": [SP1],
        "expect": [SP1_TEST],
    },
    {
        "name": "n2: children take the default parent-close policy, terminate (SP1 N2)",
        "edits": [(WORKFLOW, "ParentClosePolicy = ParentClosePolicy.Abandon,", "ParentClosePolicy = ParentClosePolicy.Terminate,")],
        "tests": [SP1],
        "expect": [SP1_TEST],
    },
    {
        "name": "no drain: the run continues-as-new with activities in flight",
        "edits": [(WORKFLOW, "if (draining && inFlightActivities == 0 && !await HandOverAsync())", "if (draining && !await HandOverAsync())"),
                  (WORKFLOW, "if (late.Count > 0 || recorded || !Workflow.AllHandlersFinished || inFlightActivities > 0)",
                   "if (late.Count > 0 || recorded || !Workflow.AllHandlersFinished)")],
        "tests": [SP1],
        "expect": [SP1_TEST],
    },
    {
        "name": "child ends that arrive while draining are not carried",
        "edits": [(WORKFLOW, "var carried = new ShellState(core.Snapshot(), children.ToList(), pendingChildEnds.ToList(),",
                   "var carried = new ShellState(core.Snapshot(), children.ToList(), new List<string>(),")],
        "tests": [SP1],
        "expect": [SP1_TEST],
    },
    {
        "name": "version guard removed from the hand-over record",
        "edits": [(WORKFLOW, "if (!handOverRecorded && Workflow.Patched(RecordHandOverPatch))", "if (!handOverRecorded && true)")],
        "tests": [("tests/FeatureFlow.Replay.Tests", None)],
        "expect": ['A_recorded_history_replays_against_the_current_workflow(name: "epic_delivery")',
                   'A_recorded_history_replays_against_the_current_workflow(name: "sp1_parent")'],
    },
    {
        "name": "a repeated answer is not recognised as a duplicate",
        "edits": [(WORKFLOW, "        if (Previous(answer) is { } previous)\n        {", "        if (false && Previous(answer) is { } previous)\n        {"),
                  (WORKFLOW, "        if (Previous(answer) is { } previous)\n            return", "        if (false && Previous(answer) is { } previous)\n            return")],
        "tests": [(INTEGRATION, "FullyQualifiedName~A_std_ticket_runs_to_completion_driven_through_the_cli")],
        "expect": ["A_std_ticket_runs_to_completion_driven_through_the_cli"],
    },
    {
        "name": "carried state drops the applied patches",
        "edits": [(CARRY, "s.Patches.Select(PatchReader.Write).ToList(),", "new List<string>(),")],
        "tests": [("tests/FeatureFlow.Interpreter.Tests", None)],
        "expect": ["A_run_carried_after_every_input_takes_the_same_path_as_one_that_is_not"],
    },
    {
        "name": "capability negotiation (check 21) removed",
        "edits": [(CHECKS, "            if (!offered.Contains(need))", "            if (false && !offered.Contains(need))")],
        "tests": [("tests/FeatureFlow.Validation.Tests", None), (INTEGRATION, "FullyQualifiedName~A_run_needing_a_capability")],
        "expect": ["Check_21_names_each_capability_no_configured_provider_offers", "A_run_needing_a_capability_no_provider_offers_is_refused_at_start"],
    },
    {
        "name": "Api references Engine (docs/11 section 2.3)",
        "edits": [(API_PROJECT, "  <ItemGroup>\n    <ProjectReference", "  <ItemGroup>\n    <ProjectReference Include=\"..\\FeatureFlow.Engine\\FeatureFlow.Engine.csproj\" />\n    <ProjectReference")],
        "tests": [("tests/FeatureFlow.Architecture.Tests", None)],
        "expect": ["Every_project_under_src_and_hosts_obeys_the_reference_rules"],
    },
]


def run_tests(tests):
    failing = set()
    for project, test_filter in tests:
        command = ["dotnet", "test", project]
        if test_filter:
            command += ["--filter", test_filter]
        run = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
        failing.update(re.findall(r"^\s+Failed [\w.]+\.(\w+(?:\(name: \"\w+\"\))?) \[", run.stdout, re.M))
        if run.returncode != 0 and not failing:
            failing.add("(build or run failed)")
            print(run.stdout[-3000:], flush=True)
    return failing


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
