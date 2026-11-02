"""Control runs for the M4 checks ("Verify, do not assert", CONTRIBUTING.md).

Each control breaks one rule in the source, runs the tests that guard it, records which fail, restores the
source, and runs the same tests again. A control holds when its named tests fail while the rule is broken and
every test passes after the revert. A name matches a failing test when the failing test's name starts with it, so
a theory is named once for all its cases; (name, n) asks for at least n of its cases.

The integration controls need a Temporal dev server (FF_TEMPORAL_ADDRESS, default localhost:7233) and Docker; they
build the sandbox image and publish the sandbox client on first use. No control calls a real model or GitHub.
Run in the working tree with no clone inside it: a stray clone breaks the layout tests.

Usage, from anywhere:  FF_TEMPORAL_ADDRESS=localhost:8033 python3 tools/check_m4_controls.py [name ...]
Exit status 1 if any control does not hold. Takes about forty minutes.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLIENT = "src/FeatureFlow.SandboxClient/EngineClient.cs"
MCP = "src/FeatureFlow.SandboxClient/McpServer.cs"
HOOK = "src/FeatureFlow.SandboxClient/HookCommand.cs"
ENDPOINT = "src/FeatureFlow.Engine.Agent/AgentEndpoint.cs"
PROXY = "src/FeatureFlow.Engine.Agent/ModelProxy.cs"
TOKENS = "src/FeatureFlow.Engine.Agent/StepTokens.cs"
POLICY = "src/FeatureFlow.Engine.Agent/HookPolicy.cs"
ACTIVITY = "src/FeatureFlow.Engine.Agent/AgentActivities.cs"
WORKFLOW = "src/FeatureFlow.Engine/RunWorkflow.cs"
RUNTIME = "src/Providers/FeatureFlow.Providers.ClaudeCode/ClaudeCodeRuntime.cs"
BROKER = "src/Providers/FeatureFlow.Providers.GitHub/AppCredentialBroker.cs"
CONFIG = "src/FeatureFlow.Model/Configuration.cs"
DOCKER = "src/Providers/FeatureFlow.Providers.Docker/DockerSandbox.cs"

INTEGRATION = "tests/FeatureFlow.Integration.Tests"
AGENT = "tests/FeatureFlow.Engine.Agent.Tests"
CLIENT_TESTS = "tests/FeatureFlow.SandboxClient.Tests"
RUNTIME_TESTS = "tests/Providers/FeatureFlow.Providers.ClaudeCode.Tests"
BROKER_TESTS = "tests/Providers/FeatureFlow.Providers.GitHub.Tests"


def it(name):
    """The integration tests whose name contains this text."""
    return (INTEGRATION, f"FullyQualifiedName~{name}")


CONTROLS = [
    {
        "name": "sp2-n1a: the sandbox client gives up at the first connection error instead of moving to another replica",
        "edits": [(CLIENT, "if (DateTimeOffset.UtcNow - failingSince > Budget)", "if (DateTimeOffset.UtcNow >= failingSince)")],
        "tests": [(CLIENT_TESTS, None), it("A_question_is_answered_after_the_agent_worker_is_restarted")],
        "expect": ["A_question_keeps_its_id_across_replicas_and_returns_the_answer", "The_hook_command_reaches_a_replica_that_comes_back_within_the_hold",
                   "A_question_is_answered_after_the_agent_worker_is_restarted"],
    },
    {
        "name": "sp2-n1b: an unreachable engine is returned to the agent as a tool error (LOG 60)",
        "edits": [(MCP, "            SessionStop.Mark(e.Message);\n            (onUnreachable ?? SessionStop.EndRuntime)();\n            return;",
                   "            text = \"engine unreachable: \" + e.Message + (onUnreachable is null ? \"\" : \"\");")],
        "tests": [(CLIENT_TESTS, None)],
        "expect": ["An_engine_unreachable_past_the_budget_ends_the_session_and_gives_the_agent_no_error"],
    },
    {
        "name": "sp2-n2: the hook command fails closed: an unreachable engine is a denial (R19)",
        "edits": [(HOOK, "            SessionStop.Mark(e.Message);\n", "            SessionStop.Mark(e.Message);\n            if (e.Message.Length > 0)\n                return 2;\n")],
        "tests": [(CLIENT_TESTS, None)],
        "expect": ["The_hook_command_holds_while_no_replica_answers_and_then_stops_the_session_rather_than_deny"],
    },
    {
        "name": "LOG 59: a question is not opened idempotently by its id",
        "edits": [(ENDPOINT, "        if (existing is not null)\n            return existing.StepKey", "        if (existing is not null && existing.Status == \"control\")\n            return existing.StepKey")],
        "tests": [(AGENT, None)],
        "expect": ["A_question_opened_twice_with_one_id_is_one_question_signalled_once"],
    },
    {
        "name": "no correction turns: the first rejected report fails the attempt",
        "edits": [(ENDPOINT, "        if (rejected > step.CorrectionTurns)", "        if (rejected > 0)"),
                  (ENDPOINT, "        return rejected + 1 > step.CorrectionTurns", "        return rejected + 1 > 0")],
        "tests": [(AGENT, None), it("A_rejected_report_is_corrected_within_the_correction_turns")],
        "expect": ["Two_rejected_reports_get_correction_turns_and_a_third_fails_the_attempt", "A_corrected_report_is_accepted",
                   "A_rejected_report_is_corrected_within_the_correction_turns"],
    },
    {
        "name": "sp5-n2: the model proxy forwards the step's token instead of the credential",
        "edits": [(PROXY, "        var credential = await options.Credential(cancellationToken);",
                   "        var credential = context.Request.Headers.Authorization.ToString().Replace(\"Bearer \", \"\", StringComparison.Ordinal);")],
        "tests": [(AGENT, None), it("An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch")],
        "expect": ["The_model_proxy_replaces_the_step_token_with_the_credential", "An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch"],
    },
    {
        "name": "F6: the worker's model credential is passed into the sandbox's environment",
        "edits": [(RUNTIME, "            [\"HOME\"] = paths.Home,",
                   "            [\"HOME\"] = paths.Home,\n            [\"CLAUDE_CODE_OAUTH_TOKEN\"] = System.Environment.GetEnvironmentVariable(\"CLAUDE_CODE_OAUTH_TOKEN\") ?? \"absent\",")],
        "tests": [(RUNTIME_TESTS, None), it("The_sandbox_holds_no_model_credential")],
        "expect": ["The_runtime_s_environment_holds_the_step_token_and_no_model_credential", "The_sandbox_holds_no_model_credential_in_its_environment_home_or_transcript"],
    },
    {
        "name": "DR19: the runtime's home is the image's, not the engine's",
        "edits": [(RUNTIME, "            [\"HOME\"] = paths.Home,", "            [\"HOME\"] = \"/home/node\",")],
        "tests": [(RUNTIME_TESTS, None), it("The_sandbox_holds_no_model_credential")],
        "expect": ["The_runtime_s_environment_holds_the_step_token_and_no_model_credential", "The_sandbox_holds_no_model_credential_in_its_environment_home_or_transcript"],
    },
    {
        "name": "DR17: the output folder is not created before the agent starts",
        "edits": [(ACTIVITY, "[paths.Checkout, paths.Output, \"/ff/inputs\", \"/ff/state\", \"/ff/tmp\"]", "[paths.Checkout, \"/ff/inputs\", \"/ff/state\", \"/ff/tmp\"]")],
        "tests": [it("Without_the_report_tool_the_agent_reports_through_a_file")],
        "expect": ["Without_the_report_tool_the_agent_reports_through_a_file_in_its_output_folder"],
    },
    {
        "name": "DR16: the runtime's MCP tool timeout is shorter than the question timeout",
        "edits": [(RUNTIME, "public static TimeSpan McpToolTimeout(AgentLimits limits) => (limits.QuestionsTimeout ?? TimeSpan.FromHours(4)) + TimeSpan.FromMinutes(15);",
                   "public static TimeSpan McpToolTimeout(AgentLimits limits) => TimeSpan.FromMinutes(1);")],
        "tests": [(RUNTIME_TESTS, None)],
        "expect": ["The_runtime_s_tool_and_hook_timeouts_exceed_the_waits_they_carry"],
    },
    {
        "name": "no commit detection: commits are pushed only when the session has ended",
        "edits": [(ACTIVITY, "if (DateTimeOffset.UtcNow >= nextPush)", "if (DateTimeOffset.UtcNow < DateTimeOffset.MinValue.AddYears(1))")],
        "tests": [it("An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch")],
        "expect": ["An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch"],
    },
    {
        "name": "pushes go to the work branch, not the hidden reference",
        "edits": [(ACTIVITY, "Must(await vcs.PushAsync(repo, hiddenRef, bundle.Head, bundle, hiddenRevision, ct), \"push to the hidden reference\");",
                   "Must(await vcs.PushAsync(repo, $\"refs/heads/{workBranch}\", bundle.Head, bundle, lastPushed, ct), \"push to the hidden reference\");")],
        "tests": [it("An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch")],
        "expect": ["An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch"],
    },
    {
        "name": "a new worker makes a new sandbox instead of re-attaching (docs/02 section 4.3)",
        "edits": [(ACTIVITY, "var attached = await sandboxes.AttachAsync(key, ct);",
                   "var attached = ProviderResult<SandboxHandle>.Failure(\"not_found\", \"control: never re-attach\");")],
        "tests": [it("AgentResumeTests")],
        "expect": ["A_question_is_answered_after_the_agent_worker_is_restarted", "A_worker_killed_mid_step_resumes_from_the_same_sandbox"],
    },
    {
        "name": "the model unreachable is read as a failed session, a work failure (SP5 N1)",
        "edits": [(RUNTIME, ": parsed.ApiError is not null ? ApiEnd(parsed.ApiStatus)", ": parsed.ApiError is \"control\" ? ApiEnd(parsed.ApiStatus)")],
        "tests": [it("With_the_model_unreachable")],
        "expect": ["With_the_model_unreachable_the_step_stops_resumably_rather_than_succeeding"],
    },
    {
        "name": "M3 follow-up 9: a cancelled step's activity is not waited for",
        "edits": [(WORKFLOW, "Workflow.Patched(WaitCancellationPatch) ? ActivityCancellationType.WaitCancellationCompleted : ActivityCancellationType.TryCancel",
                   "ActivityCancellationType.TryCancel")],
        "tests": [it("An_aborted_run_waits_for_its_agent_step_to_stop")],
        "expect": ["An_aborted_run_waits_for_its_agent_step_to_stop"],
    },
    {
        "name": "a read-only role's checkout is left writable (docs/04 section 3.7)",
        "edits": [(ACTIVITY, "            if (spec.WriteAccess == \"none\")\n                Must(await sandboxes.MakeReadOnlyAsync(",
                   "            if (spec.WriteAccess == \"never\")\n                Must(await sandboxes.MakeReadOnlyAsync(")],
        "tests": [it("A_role_with_no_write_access_cannot_change_its_checkout")],
        "expect": ["A_role_with_no_write_access_cannot_change_its_checkout"],
    },
    {
        "name": "the hook policy lets git push through",
        "edits": [(POLICY, "if (GitPush().IsMatch(command))", "if (command.Length < 0 && GitPush().IsMatch(command))")],
        "tests": [(AGENT, None), it("Hooks_deny_a_push")],
        "expect": ["A_git_push_is_denied", "A_hook_call_is_decided_from_the_token_and_recorded", "Hooks_deny_a_push_and_record_the_files_the_agent_read"],
    },
    {
        "name": "a step token's signature is not checked",
        "edits": [(TOKENS, "if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))",
                   "if (signature.Length < 0 && !CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))")],
        "tests": [(AGENT, None)],
        "expect": ["A_token_signed_with_another_key_is_refused", "A_token_whose_claims_were_changed_is_refused"],
    },
    {
        "name": "the credential broker grants what it should refuse (F5)",
        "edits": [(BROKER, "if (Refusal(actor, targetSystem, scope, ttl) is { } refusal)",
                   "if (Refusal(actor, targetSystem, scope, ttl) is { } refusal && refusal.Length < 0)")],
        "tests": [(BROKER_TESTS, None)],
        "expect": [("A_request_wider_than_the_broker_may_grant_is_refused_before_any_call", 5)],
    },
    # Added after the review of M4 (docs/build/m4-results.md section 10).
    {
        "name": "review 1: the model proxy forwards any path, to any host",
        "edits": [(PROXY, "        if (method != \"POST\" || !AllowedPaths.Contains(path))\n            return null;\n", ""),
                  (PROXY, "target.Port == upstream.Port ? target : null;", "target.Port == upstream.Port || true ? target : null;")],
        "tests": [(AGENT, None), it("The_agent_cannot_forge_engine_state")],
        "expect": [("The_model_proxy_refuses_any_request_but_the_messages_api_and_never_sends_the_credential", 6),
                   "The_proxy_s_target_keeps_the_upstream_s_scheme_host_and_port", "The_agent_cannot_forge_engine_state_change_its_hooks_or_redirect_the_proxy"],
    },
    {
        "name": "review 2a: a later execution checks out a hidden reference the branch has moved past",
        "edits": [(ACTIVITY, "\n                            && Must(await vcs.DescendsFromAsync(repo, hiddenRevision, startRevision, ct), \"compare the hidden reference with the work branch\");", ";")],
        "tests": [it("A_later_execution_starts_from_the_branch")],
        "expect": ["A_later_execution_starts_from_the_branch_when_the_hidden_reference_is_behind_it"],
    },
    {
        "name": "review 2b: the work branch is moved to a commit that does not build on it",
        "edits": [(ACTIVITY, "if (!Must(await vcs.DescendsFromAsync(repo, head, startRevision, ct)", "if (head.Length < 0 && !Must(await vcs.DescendsFromAsync(repo, head, startRevision, ct)")],
        "tests": [it("A_final_commit_that_does_not_build_on_the_branch")],
        "expect": ["A_final_commit_that_does_not_build_on_the_branch_is_not_pushed_to_it"],
    },
    {
        "name": "review 3: a new sandbox resumes a session it does not hold instead of starting afresh",
        "edits": [(ACTIVITY, "var fresh = !reattached;", "var fresh = false;")],
        "tests": [it("A_lost_sandbox_is_replaced")],
        "expect": ["A_lost_sandbox_is_replaced_and_a_fresh_session_is_told_it_was_interrupted"],
    },
    {
        "name": "review 4: the step's timeout is not enforced during supervision",
        "edits": [(ACTIVITY, "                if (DateTimeOffset.UtcNow >= deadline)\n                {", "                if (DateTimeOffset.UtcNow < DateTimeOffset.MinValue.AddYears(1))\n                {")],
        "tests": [it("A_step_past_its_timeout")],
        "expect": ["A_step_past_its_timeout_is_stopped_and_fails_as_out_of_time"],
    },
    {
        "name": "review 5a: resumes without progress are not capped",
        "edits": [(ACTIVITY, "public const int ResumesWithoutProgress = 5;", "public const int ResumesWithoutProgress = 500;")],
        "tests": [it("Resumes_without_progress")],
        "expect": ["Resumes_without_progress_stop_for_a_person_after_the_cap"],
    },
    {
        "name": "review 5c: a permanent model error is read as resumable",
        "edits": [(RUNTIME, "401 or 403 => SessionEnd.ModelRefused,", "401 or 403 => SessionEnd.ModelUnreachable,"),
                  (RUNTIME, "        _ => SessionEnd.Failed,\n    };", "        _ => SessionEnd.ModelUnreachable,\n    };")],
        "tests": [(RUNTIME_TESTS, None)],
        "expect": [("Only_transient_api_errors_are_resumable", 4)],
    },
    {
        "name": "review 6: the hook command allows a call the engine gave no decision for",
        "edits": [(HOOK, "if (reply[\"decision\"]?.ToString() == \"allow\" ||", "if (reply[\"decision\"]?.ToString() != \"deny\" ||")],
        "tests": [(CLIENT_TESTS, None)],
        "expect": ["The_hook_command_fails_closed_on_an_answer_with_no_decision"],
    },
    {
        "name": "review 7: hooks from the agent's own settings are allowed",
        "edits": [(RUNTIME, "[\"allowManagedHooksOnly\"] = true,", "[\"allowManagedHooksOnly\"] = false,")],
        "tests": [(RUNTIME_TESTS, None), it("The_agent_cannot_forge_engine_state")],
        "expect": ["Hooks_are_managed_settings_that_allow_no_other_hooks", "The_agent_cannot_forge_engine_state_change_its_hooks_or_redirect_the_proxy"],
    },
    {
        "name": "review 10: a sandbox is left behind when the step cannot answer",
        "edits": [(ACTIVITY, "catch (Exception e) when (e is StepWorkException or StepCannotAnswerException)", "catch (Exception e) when (e is StepWorkException)")],
        "tests": [it("Resumes_without_progress")],
        "expect": ["Resumes_without_progress_stop_for_a_person_after_the_cap"],
    },
    {
        "name": "review 11: a revoked step token still works",
        "edits": [(ENDPOINT, ".Unwrap().FirstOrDefault() is { Revoked: false };", ".Unwrap().FirstOrDefault() is not null;")],
        "tests": [(AGENT, None)],
        "expect": ["A_revoked_step_s_token_is_refused"],
    },
    {
        "name": "review 12: a step may open another step's question",
        "edits": [(ENDPOINT, "            return existing.StepKey == claims.StepKey\n", "            return existing.StepKey != \"\"\n")],
        "tests": [(AGENT, None)],
        "expect": ["A_question_of_another_step_is_not_shown"],
    },
    {
        "name": "review 14: engine_tools: false accepted outside development",
        "edits": [(CONFIG, "i.Settings[\"engine_tools\"].ScalarText() == \"false\"", "i.Settings[\"engine_tools\"].ScalarText() == \"never\"")],
        "tests": [("tests/FeatureFlow.Model.Tests", None)],
        "expect": ["An_installation_refuses_what_it_does_not_read"],
    },
    {
        "name": "review 17: git push is matched by a literal prefix, so /usr/bin/git push passes",
        "edits": [(POLICY, "(\\S*/)?git(", "git(")],
        "tests": [(AGENT, None)],
        "expect": [("A_git_push_is_denied", 2)],
    },
    # Added after the re-check of the review (docs/build/m4-results.md section 10).
    {
        "name": "re-check N1a: an engine-unreachable stop is believed without the engine's own evidence",
        "edits": [(ACTIVITY, "if (result.End == SessionEnd.EngineUnreachable && await LastContactAsync() is { } contact",
                   "if (result.End == \"control\" && await LastContactAsync() is { } contact")],
        "tests": [it("A_forged_engine_unreachable_stop_gets_no_resume")],
        "expect": ["A_forged_engine_unreachable_stop_gets_no_resume"],
    },
    {
        "name": "re-check N1b: a command's transcript and exit code are the agent's to write",
        "edits": [(DOCKER, "chown -R root {dir} && chmod 700 {dir}", "chown -R {user} {dir} && chmod 755 {dir}")],
        "tests": [it("The_agent_cannot_forge_engine_state")],
        "expect": ["The_agent_cannot_forge_engine_state_change_its_hooks_or_redirect_the_proxy"],
    },
    {
        "name": "re-check N2: a superseded hidden reference is overwritten, not kept",
        "edits": [(ACTIVITY, "                    Must(await vcs.PushAsync(repo, kept, hiddenRevision, null, null, ct), \"keep the superseded commits\");",
                   "                    _ = kept;")],
        "tests": [it("A_later_execution_starts_from_the_branch")],
        "expect": ["A_later_execution_starts_from_the_branch_when_the_hidden_reference_is_behind_it"],
    },
    {
        "name": "re-check N3: a hook that gates nothing blocks when it gets no decision",
        "edits": [(HOOK, " || hook != \"pre_tool_use\" && reply[\"decision\"]?.ToString() != \"deny\"", "")],
        "tests": [(CLIENT_TESTS, None)],
        "expect": [("A_hook_that_gates_nothing_does_not_block_on_an_answer_with_no_decision", 3)],
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
