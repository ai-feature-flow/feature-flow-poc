"""Control runs for the M1 load checks ("Verify, do not assert", CONTRIBUTING.md).

For each of checks 1 to 17, turns that check's errors into warnings (check 34) in the source, runs the
mutation tests in tests/FeatureFlow.Validation.Tests, records which mutations fail, and restores the
source. A check is shown to be what its mutations depend on when exactly its own mutations fail.

Usage, from anywhere:  python3 tools/check_controls.py
Exit status 1 if any check's failing set differs from its own mutations. Takes about ten minutes.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOADER = "src/FeatureFlow.Library/PlanLoader.cs"
VALIDATOR = "src/FeatureFlow.Validation/PlanValidator.cs"
READER = "src/FeatureFlow.Model/Yaml/MapReader.cs"
MUTATIONS = "tests/FeatureFlow.Validation.Tests/MutationTests.cs"

CONTROLS = [
    (1, [READER], "Checks.Schema"),
    (2, [LOADER, VALIDATOR], "Checks.UnknownAction"),
    (3, [VALIDATOR], "Checks.DuplicateId"),
    (4, [LOADER, VALIDATOR], "Checks.UndeclaredParameter"),
    (5, [VALIDATOR], "Checks.Expression"),
    (6, [VALIDATOR], "Checks.Outcomes"),
    (7, [LOADER], "Checks.OutcomeRouted"),
    (8, [LOADER, VALIDATOR], "Checks.RouteTarget"),
    (9, [VALIDATOR], "Checks.Reachable"),
    (10, [VALIDATOR], "Checks.CycleExit"),
    (11, [VALIDATOR], "Checks.ArtifactProducer"),
    (12, [VALIDATOR], "Checks.EvidenceType"),
    (13, [VALIDATOR], "Checks.WriteAccess"),
    (14, [LOADER, VALIDATOR], "Checks.Fragment"),
    (15, [LOADER], "Checks.ParallelRoutes"),
    (16, [VALIDATOR], "Checks.DecisionType"),
    (17, [VALIDATOR], "Checks.PlanSize"),
]


def expected_by_check():
    text = open(os.path.join(ROOT, MUTATIONS)).read()
    result = {}
    for name, check in re.findall(r'\["(\w+)"\] = new\((\d+),', text):
        result.setdefault(int(check), set()).add(name)
    return result


def main():
    expected = expected_by_check()
    ok = True
    for check, files, constant in CONTROLS:
        originals = {}
        for f in files:
            path = os.path.join(ROOT, f)
            text = open(path).read()
            originals[path] = text
            open(path, "w").write(re.sub(r"(\.Add\()" + re.escape(constant) + r",", r"\1Checks.UnusedParameter,", text))
        try:
            run = subprocess.run(
                ["dotnet", "test", "tests/FeatureFlow.Validation.Tests", "--filter", "FullyQualifiedName~MutationTests"],
                cwd=ROOT, capture_output=True, text=True)
        finally:
            for path, text in originals.items():
                open(path, "w").write(text)
        failing = set(re.findall(r'Failed FeatureFlow\.Validation\.Tests\.MutationTests\.\w+\(name: "(\w+)"\)', run.stdout))
        own = expected.get(check, set())
        verdict = "ok" if failing == own and own else "MISMATCH"
        ok = ok and verdict == "ok"
        print(f"check {check:2}: {verdict}; failing {sorted(failing)}", flush=True)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
