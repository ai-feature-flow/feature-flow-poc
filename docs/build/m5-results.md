# M5 results

Source control, testing and the first real ticket (`docs/11-build-plan.md`, M5). Two builders worked in
parallel; each owns a section here.

---

## Engine actions and checks

Branch `m5-actions`, from `m5@1189a6b` (the interfaces and in-memory fakes, LOG 107).

### What was built

| Part | Where | What |
|---|---|---|
| Ten actions | `src/FeatureFlow.Engine/Actions/` | `scm.catch_up`, `scm.pull_request`, `scm.merge`, `scm.revert` (`SourceControl.cs`); `ci.checks`, `wait.condition` (`Waiting.cs`); `test.run`, `test.integration`, `analysis.run`, `repo.inspect` (`Testing.cs`). Dispatched by `RunActionAsync` when a real VCS and code host are configured; other actions stay on the stand-in (LOG 121) |
| Result parsing | `Actions/ResultFiles.cs` | TRX, JUnit and SARIF into the `Evidence.cs` records, one place for sandbox and CI tiers |
| Evidence | `Actions/Evidence.cs` | The JSON of `test_results`, `check_results`, `analysis_results`, `merge_record` (LOG 132) |
| Waiting | `RunActionAsync` | A wait is one read per activity attempt; an open wait fails retryably with the poll interval as the next delay, so it holds no worker (LOG 122) |
| Run-start checks 20, 23, 24 | `Activities.cs` `RepositoryChecksAsync`, `Validation/RunStartChecks.cs` | Read the default branch, its protection and `feature-flow.yaml` through the code host |
| Step-start checks 31, 33 | `Validation/StepStartChecks.cs` | 31: each M5 action's inputs; 33: `tier_tags` against the manifest read at the step |
| Step-start check 32 | `RunWorkflow.cs` | A consumed artifact no step produced stops the step (patch `m5-step-start`, LOG 127) |
| Repository manifest | `Model/Manifest.cs` | `feature-flow.yaml` as `docs/07` section 4 writes it; unknown keys refused |
| F1 setting | `Model/Configuration.cs` | `policy.merge.default_branch.engine_may_merge`, carried in the run's policy |
| `clean_up` reset | `EngineActions.CleanUpAsync`, `RunWorkflow.cs` | Expected-revision push back to where the step started (patch `m5-clean-up`, LOG 128) |
| Agent step | `Engine.Agent/AgentActivities.cs` | Parent branch from the code host; manifest `setup` and `after_changes` run in the sandbox; start revision recorded (LOG 129, 135) |
| Memory sandbox | `Providers.Local/MemorySandbox.cs` | Scripted commands for engine tests, no container (LOG 136) |

`test.integration` is `test.run` with the `integration` tag across repositories; std's name works unchanged
(LOG 120).

### Checks

| What | Test | Result |
|---|---|---|
| Each action, with the in-memory world | `FeatureFlow.Engine.Tests`: `SourceControlActionTests` (16), `WaitingActionTests` (10), `TestingActionTests` (15), `ResultFileTests` (11 cases) | Pass |
| Checks 20, 23, 24 on std's `ticket_delivery` | `Engine.Tests/RunStartCheckTests` (5) | Pass |
| **F1**: a standalone ticket with `engine_can_merge: true` into the default branch is refused at run start | `RunStartCheckTests.F1_a_standalone_ticket_...` and, through the engine and CLI, `ForgeTicketTests.F1_through_the_engine_...` (run fails, detail cites check 23; with std's default `false` the same ticket starts) | Pass |
| std `ticket_delivery` to a merge into its parent branch, on the `memory` providers with the scripted runtime | `Integration.Tests/ForgeTicketTests.A_std_ticket_runs_...`: every step through `deliver.merge`; one squash commit on `main` holding exactly the tested head's tree, with the `Feature-Flow-Merge` trailer; the pull request closed with a link to the commit | Pass |
| Check 32 | `Integration.Tests/StepStartCheckTests` (2, with a control), test library `libraries/m5` | Pass |
| Agent step: parent branch, `setup`, `after_changes` | `Engine.Agent.Tests/AgentManifestTests` (5) | Pass |
| Manifest reader, patterns, `engine_may_merge` | `Model.Tests/ManifestTests` (17 cases) | Pass |
| Unchanged behaviour | `TicketRunTests` (5), `EpicRunTests`, `StepFailureTests`, `ContinueAsNewTests`; replay tests | Pass |

Counts: default suite **510 passed, 0 failed** (431 at `1189a6b`). Integration tests were run locally on a dev
server, not the Docker agent tests: the agent step's change with no code host keeps M4's behaviour, and CI runs them.

The engine tests found one bug of their own: `ci.checks` read the clock before awaiting the wait's start time,
so its first read saw a negative elapsed time and waited out a zero grace. Fixed; the tests then ran ten times
with no failure.

### Controls

`tools/check_m5_actions_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker). Each breakage was run, its tests
failed, and all passed again after the revert. **All 20 hold.**

| Breakage | Tests that failed |
|---|---|
| `require_tested` skipped | `A_head_without_passing_test_evidence_is_not_merged` |
| `require_checks` skipped | `A_head_with_failed_checks_is_not_merged_...` |
| F1 not re-checked at merge time | `F1_at_merge_time_turns_..._into_an_action_request` |
| Check 23 skipped | 3 `RunStartCheckTests` F1 cases and `ForgeTicketTests.F1_through_the_engine_...` |
| Check 24 skipped | `Check_24_an_unprotected_default_branch_is_refused` |
| Check 20 not called | `Check_20_..._overrides_is_refused_...` |
| `ci.checks` ignores expected CI runs | `An_early_third_party_check_does_not_pass_while_an_expected_CI_run_has_not_reported` |
| `ci.checks` ignores a head change | `A_head_that_changes_while_checks_are_awaited_...` |
| Check 31 skipped | `Check_31_refuses_...`, `Wait_condition_..._takes_one_condition_only` |
| Check 33 skipped | `Check_33_a_tag_that_matches_no_tier_...` |
| Check 32 skipped | `Check_32_a_step_whose_consumed_artifact_was_never_produced_...` |
| A carried result adopted for another head | `A_head_already_tested_..._adopts_the_result_...` |
| TRX failures counted as passes | `TRX_counts_outcomes_...`, `Failed_tests_are_failed_...` |
| Analysis ignores the baseline | `Analysis_fails_on_new_issues_against_the_baseline_...` |
| `clean_up` does not reset the branch | `A_clean_up_resets_the_work_branch_...` |
| `clean_up` leaves hidden references | the same test |
| Actions' parent branch hard-coded `main` | `A_pull_request_is_opened_into_the_default_branch_read_from_the_code_host_...` |
| Agent's parent branch hard-coded `main` | `The_parent_branch_is_the_default_branch_the_code_host_names` |
| Agent `setup` not run | `Setup_runs_before_the_agent_...`, `A_failing_setup_command_...`, `After_changes_does_not_run_...` |
| Agent `after_changes` not run | `Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_...` |

### The live run (M5's done-when), for the coordinator

Not run here: it needs the providers builder's Git, GitHub and GitHub Actions providers. What it needs:

1. **The fixture's manifest.** `ai-feature-flow/feature-flow-example`'s `feature-flow.yaml` is in the first
   design's format (`apiVersion`, `testTiers`, `staticAnalysis`, `branches`, `merge`). Every step that reads it
   would stop for a person: unknown keys are refused (`docs/07` section 4). A replacement in the current format:

   ```yaml
   api_version: feature-flow/v1
   kind: repository
   setup: { commands: ["dotnet restore"] }
   test_tiers:
     - name: unit
       tags: [unit]
       command: dotnet test tests/FeatureFlow.Example.Api.UnitTests --filter Category=unit --logger "trx;LogFileName=unit.trx" --results-directory TestResults
       result: { format: trx, path: TestResults/unit.trx }
       targets: [sandbox, ci]
       ci: { job: unit.yml }
     - name: integration
       tags: [integration]
       command: docker compose -f compose.integration.yaml up --abort-on-container-exit --exit-code-from integration-tests
       targets: [ci]
       ci: { job: integration.yml }
   static_analysis: { analyser: none }
   ```

   The unit tier runs in the sandbox when one is configured, so the sandbox image needs the .NET SDK the fixture
   targets (`net9.0`, run with `DOTNET_ROLL_FORWARD=Major` on SDK 10); otherwise drop `sandbox` from its targets.
2. **Branch protection on `main`** (check 24). It is not protected now, so every run would be refused at start.
3. **Who merges.** Recommended: std's default, `engine_can_merge: false`. The run opens the pull request, waits
   for checks, catches up, and the merge step waits for a person (polled every 30 seconds); merge the pull request
   on GitHub (for example `GH_TOKEN="$SPARKKZ_NZ_GITHUB_TOKEN" gh pr merge <n> --squash`) and the run completes
   `merged`. For an engine merge instead: `policy.merge.default_branch.engine_may_merge:
   [ai-feature-flow/feature-flow-example]`, `--param engine_can_merge=true`, and protection that lets the App push
   to `main`; protection requiring a pull request refuses the engine's push, which turns the step into the same
   action request.
4. Installation: `vcs` git, `code_host` github, `ci` github_actions, `sandbox` docker, `agent_runtime`
   claude_code, as in M4's live tests. Start with
   `ff run start std:templates/ticket_delivery --param ticket_id=<id> --repo ai-feature-flow/feature-flow-example`.

### Deviations and judgement calls

LOG 120 to 136, unreviewed. Three narrow the design and need a look: **120** (`test.integration` starts no
services), **123** (the action request is watched on the code host; no decision record yet). LOG 121 keeps the
stand-in for runs with only fake providers.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Check 23 at patch time; the merge-time re-check covers it meanwhile |
| 2 | The action request's decision record and an API answer for it (M7) |
| 3 | `test.integration` starting services from `integration_environment` |
| 4 | `analysis.run` with `sonarcloud`; `wait.condition` on a tracker issue |
| 5 | The carried test result by tree identity, not only by revision |
| 6 | `run.parent_branch` as an expression value is still `main` |
| 7 | A person ending a wait by hand with `met` or `unmet` (`docs/04` section 8.2) |
| 8 | Inbound events to wake waits early; waits poll every 30 seconds |
| 9 | The GitHub code host must read branch protection with the App's permissions (Administration: read); a 403 there is an infrastructure error and run start retries |
