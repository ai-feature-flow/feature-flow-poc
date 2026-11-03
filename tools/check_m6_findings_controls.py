"""Control runs for M6's findings ledger, findings.record, findings.triage, the findings namespace and the Copilot review
findings source ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the source, and
runs the same tests again. A control holds when its named tests fail while the rule is broken and every test passes
after the revert. A name matches a failing test when the failing test's name starts with it, so a theory is named once
for all its cases; (name, n) asks for at least n of its cases.

The integration controls need a Temporal dev server (FF_TEMPORAL_ADDRESS, default localhost:7233). No control needs
Docker, a real model or GitHub: they run on the in-memory providers and a recorded GitHub.

Usage, from anywhere:  FF_TEMPORAL_ADDRESS=localhost:8233 python3 tools/check_m6_findings_controls.py [name ...]
Exit status 1 if any control does not hold.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STORES = "src/Providers/FeatureFlow.Providers.Local/Stores.cs"
FINDINGS = "src/FeatureFlow.Engine/Actions/Findings.cs"
LEDGER = "src/FeatureFlow.Providers.Abstractions/FindingsLedger.cs"
SCOPE = "src/FeatureFlow.Engine/Actions/ActionScope.cs"
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
CARRY = "src/FeatureFlow.Interpreter/Carry.cs"
AGENT_ACTIVITY = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"
LOADER = "src/FeatureFlow.Library/PlanLoader.cs"
FIX_LOOP = "library/std/fragments/fix_loop.yaml"
COPILOT = "src/Providers/FeatureFlow.Providers.GitHub/CopilotReviewSource.cs"

LOCAL = ("tests/Providers/FeatureFlow.Providers.Local.Tests", "FullyQualifiedName~Sqlite")
ENGINE = ("tests/FeatureFlow.Engine.Tests", "FullyQualifiedName~FindingsActionTests")
CORE = ("tests/FeatureFlow.Interpreter.Tests", "FullyQualifiedName~FindingsNamespaceTests")
VALIDATION = ("tests/FeatureFlow.Validation.Tests", "FullyQualifiedName~StdLibraryTests")
WORKFLOWS = ("tests/FeatureFlow.Testing.Workflows.Tests", None)
REPLAY = ("tests/FeatureFlow.Replay.Tests", None)
COPILOT_TESTS = ("tests/Providers/FeatureFlow.Providers.GitHub.Tests", "FullyQualifiedName~Copilot&Category!=Live")
LOOP = ("tests/FeatureFlow.Integration.Tests", "FullyQualifiedName~FindingsLoopTests")


CONTROLS = [
    # ---- The SQLite ledger ----
    {
        "name": "ledger: a run's view leaves out its descendants",
        "edits": [(STORES, "UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE runs.org_id = $o)",
                   "UNION SELECT runs.run_id FROM runs JOIN tree ON runs.parent_run_id = tree.id WHERE 0)")],
        "tests": [LOCAL, ENGINE],
        "expect": ["A_runs_view_holds_its_descendants_findings_and_not_its_parents_or_siblings",
                   "A_parents_review_re_raises_a_childs_finding_in_the_childs_ledger"],
    },
    {
        "name": "ledger: a finding written again keeps the first write",
        "edits": [(STORES, "INSERT OR REPLACE INTO findings", "INSERT OR IGNORE INTO findings")],
        "tests": [LOCAL, ENGINE],
        "expect": ["A_finding_written_again_keeps_the_last_write", "A_fixed_finding_re_raised_after_its_file_changed_reopens_with_code_change"],
    },
    {
        "name": "ledger: a repeated event key is appended again",
        "edits": [(STORES, "INSERT OR IGNORE INTO finding_events", "INSERT OR REPLACE INTO finding_events")],
        "tests": [LOCAL],
        "expect": ["A_findings_history_keeps_its_order_and_a_repeated_key_adds_nothing"],
    },
    # ---- findings.record ----
    {
        "name": "record: every finding is new (no fingerprint matching)",
        "edits": [(FINDINGS, "var existing = Match(ledger, fingerprint, run.Call.Step.RunId);", "FindingRecord? existing = null;")],
        "tests": [ENGINE],
        "expect": ["The_same_finding_raised_again_is_a_re_raise_not_a_new_finding_and_line_moves_do_not_matter",
                   "A_parents_review_re_raises_a_childs_finding_in_the_childs_ledger"],
    },
    {
        "name": "record: reopen_on is ignored, every re-raise of a fixed or declined finding reopens it",
        "edits": [(FINDINGS, "&& (reopenOn == Always || (reopenOn == CodeChange && changed));", "&& true;")],
        "tests": [ENGINE],
        "expect": ["A_fixed_finding_re_raised_on_unchanged_code_stays_fixed_by_default_and_counts_as_reraised_unchanged",
                   "Reopen_on_decides_whether_a_re_raised_declined_finding_reopens"],
    },
    {
        "name": "record: a change at the finding's file is not seen",
        "edits": [(FINDINGS, "return await FileAsync(location, since) != await FileAsync(location, head);",
                   "return await FileAsync(location, since) != await FileAsync(location, head) && false;")],
        "tests": [ENGINE],
        "expect": ["A_fixed_finding_re_raised_after_its_file_changed_reopens_with_code_change", "A_finding_reopened_after_two_fixes_is_a_repeat_fix"],
    },
    {
        "name": "record: any new commit counts as a change at the finding",
        "edits": [(FINDINGS, "return await FileAsync(location, since) != await FileAsync(location, head);",
                   "return await FileAsync(location, since) != await FileAsync(location, head) || since != head;")],
        "tests": [ENGINE],
        "expect": ["A_change_to_another_file_is_not_a_change_at_the_finding"],
    },
    {
        "name": "record: a retry applies its findings again",
        "edits": [(FINDINGS, "if (await history.ByThisStepAsync(existing) is { } done)", "if (await history.ByThisStepAsync(existing) is { } done && false)")],
        "tests": [ENGINE],
        "expect": ["A_retried_record_changes_nothing_more_and_reports_the_same"],
    },
    {
        "name": "record: a report that is not a findings report is recorded",
        "edits": [(FINDINGS, "return errors.Count == 0 ? report :", "return errors.Count >= 0 ? report :")],
        "tests": [ENGINE],
        "expect": [("A_report_that_is_not_a_findings_report_stops_the_step_for_a_person", 3)],
    },
    {
        "name": "record: a recorder in a for_each branch reads another branch's report",
        "edits": [(FINDINGS, ".OrderByDescending(a => a.StepId.StartsWith(prefix, StringComparison.Ordinal))", ".OrderByDescending(a => prefix.Length < 0)")],
        "tests": [ENGINE],
        "expect": ["A_recorder_in_a_for_each_branch_reads_its_own_branchs_report"],
    },
    {
        "name": "record: an external review pending for ever is waited on for ever",
        "edits": [(FINDINGS, "case CollectStates.Pending when DateTimeOffset.UtcNow - since < run.Options.ReviewTimeout:",
                   "case CollectStates.Pending when DateTimeOffset.UtcNow - since < run.Options.ReviewTimeout * 100000:")],
        "tests": [ENGINE],
        "expect": ["A_review_pending_past_the_timeout_is_unavailable"],
    },
    # ---- The findings namespace ----
    {
        "name": "namespace: repeat_fixes counts a finding fixed once",
        "edits": [(LEDGER, "finding.Fixes >= 2", "finding.Fixes >= 3")],
        "tests": [ENGINE],
        "expect": ["A_finding_reopened_after_two_fixes_is_a_repeat_fix"],
    },
    {
        "name": "namespace: open_blocking counts every open finding",
        "edits": [(LEDGER, '["open_blocking"] = list.Count(f => Open(f) && f.Finding.Blocking),', '["open_blocking"] = list.Count(Open),')],
        "tests": [ENGINE],
        "expect": ["A_reviewers_finding_is_recorded_open_with_its_history_and_the_namespace_counts_it"],
    },
    {
        "name": "namespace: the workflow does not give the core the counts",
        "edits": [(WORKFLOW, "if (outcome.Findings is { } counts)", "if (outcome.Findings is { } counts && counts.Count < 0)")],
        "tests": [LOOP],
        "expect": ["A_blocking_finding_goes_through_std_s_fix_loop_on_the_real_ledger_and_the_ticket_then_merges"],
    },
    {
        "name": "namespace: the activity's outcome drops the counts",
        "edits": [(SCOPE, "new(Kind, Report, Reason, Events) { Findings = Findings };", "new(Kind, Report, Reason, Events) { Findings = null };")],
        "tests": [LOOP],
        "expect": ["A_blocking_finding_goes_through_std_s_fix_loop_on_the_real_ledger_and_the_ticket_then_merges"],
    },
    {
        "name": "namespace: the counts are lost at the hand-over",
        "edits": [(CARRY, "            s.Findings[name] = count;", "            _ = (name, count);")],
        "tests": [CORE],
        "expect": [("An_open_blocking_finding_sends_the_merge_bar_to_the_fix_loop_and_none_lets_it_through", 1),
                   "The_counts_are_carried_over_a_hand_over"],
    },
    # ---- The ledger view ----
    {
        "name": "ledger view: check 32 refuses a step consuming findings_ledger",
        "edits": [(WORKFLOW, " && c.Name != FindingsLedger.ViewName", "")],
        "tests": [LOOP],
        "expect": ["A_blocking_finding_goes_through_std_s_fix_loop_on_the_real_ledger_and_the_ticket_then_merges"],
    },
    {
        "name": "ledger view: the agent worker does not stage it",
        "edits": [(AGENT_ACTIVITY, "var reference = name == FindingsLedger.ViewName ? await LedgerViewAsync()",
                   "var reference = name == FindingsLedger.ViewName && name.Length < 0 ? await LedgerViewAsync()")],
        "tests": [LOOP],
        "expect": ["A_blocking_finding_goes_through_std_s_fix_loop_on_the_real_ledger_and_the_ticket_then_merges"],
    },
    # ---- findings.triage ----
    {
        "name": "triage: an entry naming an unknown finding is not refused",
        "edits": [(FINDINGS, "if (!ledger.ContainsKey(id))", "if (id.Length < 0)")],
        "tests": [ENGINE],
        "expect": ["A_triage_naming_an_unknown_finding_is_invalid_and_applies_nothing"],
    },
    {
        "name": "triage: a retry applies the triage again",
        "edits": [(FINDINGS, "if (await history.ByThisStepAsync(before) is not null)", "if (await history.ByThisStepAsync(before) is not null && before.Fixes < 0)")],
        "tests": [ENGINE],
        "expect": ["A_retried_triage_applies_nothing_again_even_after_the_finding_moved_on"],
    },
    {
        "name": "triage: a move to fixed is not counted as a fix",
        "edits": [(FINDINGS, "Fixes = before.Fixes + (t.State == FindingStates.Fixed ? 1 : 0),", "Fixes = before.Fixes,")],
        "tests": [ENGINE],
        "expect": ["A_triage_applies_each_state_with_its_actor_and_reason_and_the_namespace_follows", "A_finding_reopened_after_two_fixes_is_a_repeat_fix"],
    },
    {
        "name": "triage: declines are never posted",
        "edits": [(FINDINGS, 'if (run.Bool("post_declines", true))', 'if (run.Bool("post_declines", true) && applied < 0)')],
        "tests": [ENGINE],
        "expect": ["A_decline_is_posted_once_on_the_pull_request_and_not_when_post_declines_is_false"],
    },
    {
        "name": "triage: post_declines false still posts",
        "edits": [(FINDINGS, 'if (run.Bool("post_declines", true))', 'if (run.Bool("post_declines", true) || applied >= 0)')],
        "tests": [ENGINE],
        "expect": ["A_decline_is_posted_once_on_the_pull_request_and_not_when_post_declines_is_false"],
    },
    {
        "name": "triage: a decline's comment key is not stable, so a retry posts twice",
        "edits": [(FINDINGS, '$"{run.Key}/{finding.FindingId}"', '$"{run.Key}/{finding.FindingId}/{Guid.NewGuid()}"')],
        "tests": [ENGINE],
        "expect": ["A_decline_is_posted_once_on_the_pull_request_and_not_when_post_declines_is_false"],
    },
    # ---- invalid: routing and libraries from before it ----
    {
        "name": "invalid: an older library that does not route it no longer loads",
        "edits": [(LOADER, "?.Escalates?.Contains(outcome.Name) == true)", "?.Escalates?.Contains(outcome.Name) == true && outcome.Name.Length < 0)")],
        "tests": [VALIDATION, REPLAY],
        "expect": ["A_fix_loop_written_before_findings_triage_could_be_invalid_still_loads_and_escalates_it",
                   ("A_recorded_history_replays_against_the_current_workflow", 3)],
    },
    {
        "name": "invalid: std sends an invalid triage somewhere other than its producer",
        "edits": [(FIX_LOOP, "      invalid: triage ", "      invalid: fix ")],
        "tests": [WORKFLOWS],
        "expect": ["Std_workflow_test_passes"],
    },
    # ---- The Copilot review findings source ----
    {
        "name": "copilot: a review of an older head counts as ready",
        "edits": [(COPILOT, ' && r["commit_id"]?.GetValue<string>() == target.Revision)', ")")],
        "tests": [COPILOT_TESTS],
        "expect": ["Copilot_is_asked_once_then_pending_until_it_reviews_the_revision"],
    },
    {
        "name": "copilot: Copilot is asked again while already asked",
        "edits": [(COPILOT, "        if (asked.Value)\n            return Handle(handle);", "        if (asked.Value && handle.PullRequest < 0)\n            return Handle(handle);")],
        "tests": [COPILOT_TESTS],
        "expect": ["Copilot_is_asked_once_then_pending_until_it_reviews_the_revision"],
    },
    {
        "name": "copilot: a request GitHub accepts without adding Copilot waits for a review that never comes",
        "edits": [(COPILOT, '.Any(IsCopilot) ? handle : handle with { Refused = "ignored" }', '.Any(IsCopilot) || true ? handle : handle with { Refused = "ignored" }')],
        "tests": [COPILOT_TESTS],
        "expect": ["A_request_github_accepts_without_adding_copilot_is_unavailable"],
    },
    {
        "name": "copilot: the snippet is the whole diff hunk, so a moved hunk is a new finding",
        "edits": [(COPILOT, "lines.TakeLast(count)", "lines")],
        "tests": [COPILOT_TESTS],
        "expect": ["Each_review_comment_becomes_a_finding_on_the_lines_it_is_about_with_a_suggestion_as_its_correction",
                   "A_comment_on_unchanged_lines_has_the_same_fingerprint_after_the_hunk_moves"],
    },
    {
        "name": "copilot: the instance's blocking setting is ignored",
        "edits": [(COPILOT, '"medium", blocking, FindingBases.Reasoned', '"medium", blocking && false, FindingBases.Reasoned')],
        "tests": [COPILOT_TESTS],
        "expect": ["The_instance_can_make_copilots_findings_blocking"],
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
