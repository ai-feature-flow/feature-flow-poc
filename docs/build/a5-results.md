# A5 results: sandboxes and runtimes

Milestone A5 of `docs/12-build-plan-phase-2.md` section 4, built 2026-11-21 on `stuart-parkinson_xero/feature-flow-a5`
from three parallel builder tasks (F4; F12 with L5's `engine_tools`, after a design note; L5's `api.base_url` with
L10), an independent review and one fix pass. Every done-when item is built and shown by a test seen to fail when the
code it guards is broken. No live run was needed. No replay history changed.

## What was built

| Item | Change |
|---|---|
| F4 | A repository names its sandbox image in the manifest's `environment.image`; the engine passes it as `SandboxSpec.Image`. The agent step uses its work branch's manifest; `test.run` and `analysis.run` use the parent branch's, as for `setup`, so an image the agent names never shapes evidence; a publication's sandbox uses the installation's image (LOG 440). The Docker and Kubernetes sandboxes use a repository image only when it is their own `image` or matches their new `allowed_images` setting, matched with case; any other stops the step for a person as `cannot_answer` (`SandboxImages.Choose`, LOG 441). `environment` takes only `image`; `from`, `os` and `docker` are refused (LOG 442). The base sandbox image's contract is in `docs/06` section 3.1 (LOG 443). M4 follow-up 1 is restored as M8 follow-up 28 |
| F12 | `SandboxedAgentRuntime` (`src/FeatureFlow.Providers.Abstractions/AgentRuntimes.cs`) holds the session lifecycle; Claude Code and Copilot CLI are subclasses with their own command line, environment, files, transcript parser, turn marker, one hook table (offered tools plus companions) and replies (LOG 445, 449). Hooks are read by the step's runtime: the step token names its runtime instance, the instance reads the hook input as a tool class, `HookPolicy` decides on classes and names no tool, and the sandbox client applies the runtime's denial reply and, when the engine is unreachable, the runtime's own reply from `FF_HOOK_UNREACHABLE`, failing closed with exit 2 when it has none (LOG 444). `model_access` is read per runtime instance, each with its own model proxy chosen by the step token; an instance without one is not served (LOG 446). The agent runtime contract suite (`src/FeatureFlow.Providers.Contracts/AgentRuntimeContract.cs`) runs both runtimes in the default category against a scripted sandbox and a stand-in for each CLI with its own tool table (LOG 447) |
| L5 | `engine_tools` and the file report removed from both runtimes, the agent activity, `AgentTask`, `AgentResult` and std's reporting preamble; naming it is refused as an unknown setting (LOG 448). `api.base_url` removed and refused as an unknown key (LOG 450) |
| L10 | `ProviderError.Hint`, set at a provider's boundary with `WithFaultHint`: GitHub's API client and App broker keep the App text, Jira names its secret, Slack its bot token, every other provider gets a neutral default (LOG 451) |
| Documents | `docs/02` section 4; `docs/04` section 3.3; `docs/06` sections 1, 1.1, 3.1, 3.2, 3.13; `docs/07` sections 2 and 4; `docs/10` Q3 and R23; `docs/build/m8-results.md` follow-ups 14, 15 and 28; `tools/check_m4_controls.py` retargeted. `docs/12` added no "planned" marker for A5's items |

## Checks

| Done when | Test |
|---|---|
| A run on a repository that declares an image runs its tests in that image (integration test with a local image) | `SandboxImageTests.A_repository_that_names_an_image_runs_its_tests_in_that_image`: builds `feature-flow-f4-test:1` `FROM` the base image with a marker command, which the repository's only test tier runs ("1 passed"). Controls: `A_repository_that_names_no_image_runs_its_tests_in_the_installations_image` (the same tier fails), `An_image_the_installation_does_not_allow_stops_the_step_for_a_person`. Default category: `TestingActionTests.Test_and_analysis_sandboxes_are_made_from_the_parent_branchs_environment_image`, `AgentManifestTests.The_agents_sandbox_is_made_from_the_manifests_environment_image`, `SandboxImagesTests`, the Docker sandbox contract's refusal case |
| Both built-in runtimes pass the runtime contract suite; a deliberately broken hook mapping fails it | `ClaudeCodeRuntimeContract` and `CopilotCliRuntimeContract` (about 23 cases each), among them `Every_tool_the_cli_reports_is_read_as_its_class`, `Only_the_role_s_tool_classes_are_offered`, `A_denial_blocks_the_call_and_shows_the_agent_the_reason`, `The_engine_unreachable_reply_blocks_the_call`, and the lifecycle, continuation, attach and API-error cases. Broken mappings below |
| Configuration naming `engine_tools` or `api.base_url` is refused | `CompositionTests.Engine_tools_is_refused_on_either_built_in_runtime` (both kinds); `ConfigurationTests.An_installation_refuses_what_it_does_not_read` (`api.base_url`) |
| `docs/06` states the base sandbox image's contract, and M4 follow-up 1 is back on the follow-up list | `docs/06` section 3.1, "The base sandbox image"; `docs/build/m8-results.md` follow-up 28 |

Suites on the merged branch: see [Verification](#verification).

## Controls

Each broke the guarded code, ran the test, saw it fail, and restored it. The builders and fixer recorded about 35 such
checks (their reports); a sample:

| Mutation | Test seen to fail |
|---|---|
| The Docker sandbox creates from its own image, ignoring the chosen one | `SandboxImageTests.A_repository_that_names_an_image_runs_its_tests_in_that_image` |
| `SandboxImages.Choose` allows any image | `SandboxImagesTests`, the Docker contract's refusal case, `SandboxImageTests.An_image_the_installation_does_not_allow_…` |
| `test.run`'s sandbox passes no image | `TestingActionTests.Test_and_analysis_sandboxes_are_made_from_the_parent_branchs_environment_image` |
| Claude Code's `Write` moved from `edit` to `read`; Copilot's `create` moved likewise; a companion tool moved | `Every_tool_the_cli_reports_is_read_as_its_class` (naming the tool), `Only_the_role_s_tool_classes_are_offered` |
| A runtime's unreachable reply that does not block; the client allowing a call with no reply | `The_engine_unreachable_reply_blocks_the_call`; `SandboxClientTests.The_hook_command_blocks_the_call_when_the_engine_is_unreachable_and_the_runtime_gave_no_reply` |
| The model proxy read from the default instance only; the proxy chosen ignoring the token | `CompositionTests.Each_agent_runtime_instance_has_the_model_proxy_of_its_own_model_access`; `EndpointTests.A_step_s_model_proxy_is_its_runtime_instance_s` |
| `engine_tools` back in Claude Code's settings | `CompositionTests.Engine_tools_is_refused_on_either_built_in_runtime(claude_code)` |
| `api.String("base_url")` restored | `ConfigurationTests.An_installation_refuses_what_it_does_not_read` |
| The old GitHub-only `DescribeFault` text | `JiraTrackerTests`, `SlackNotificationsTests`, `ConfigurationFaultTests` |
| `AgentEndpoint`'s JSON in PascalCase | `HookWireTests` (3) |

The sub-orchestrator repeated three independently on the merged branch: see [Verification](#verification).

## Verification

On the merged branch at its last code commit (`a391a4a`), on a local Temporal dev server and Docker:

| Suite | Result |
|---|---|
| Default (`Category!=Integration&Category!=Live&Category!=Cluster`) | 1,578 passed, 0 failed, 24 projects; replay 42 of 42 |
| Integration | Provider projects all passed (Docker 17, Postgres 26, S3 12). `FeatureFlow.Integration.Tests`: 119 of 123 in a full run, while another milestone's Integration suites ran against the same Docker daemon and CPU; a full run during a builder's build gave 120 of 123 with a different set of failures. The union of failing tests (agent unreachable model, Copilot unreachable model, hooks denying a push, worker killed mid-step, no model credential in the sandbox, checkout at the parent head) and `SandboxImageTests` passed together, alone: 9 of 9. The failures were timeouts, a heartbeat timeout and a stopped container, not assertions on A5 behaviour; see follow-up 8 |

Break and see fail, by the sub-orchestrator on the merged branch (each restored, then seen to pass):

| Mutation | Test seen to fail |
|---|---|
| Claude Code's `Write` moved from `edit` to `read` | `ClaudeCodeRuntimeContract.Every_tool_the_cli_reports_is_read_as_its_class` and two `Only_the_role_s_tool_classes_are_offered` rows |
| The Docker sandbox creates from its own image | `SandboxImageTests.A_repository_that_names_an_image_runs_its_tests_in_that_image` |
| `engine_tools` listed again in Claude Code's settings | `CompositionTests.Engine_tools_is_refused_on_either_built_in_runtime(claude_code)` |
| `api.String("base_url")` restored | `ConfigurationTests.An_installation_refuses_what_it_does_not_read` (`base_url` row) |

## Size

`src` grew by about 640 lines (+1,286, −647), `tests` by about 500. `ClaudeCodeRuntime.cs` went from 362 to 213 lines and
`CopilotCliRuntime.cs` from 396 to 253; the shared 337-line base class replaces about 240 copied lines, and
`HookPolicy.cs` went from 73 to 59 lines with no tool names. The growth is the 496-line runtime contract suite, the
image choice and its setting, and per-instance proxies. Concepts removed: the `engine_tools` switch and file report,
`AgentTask.EngineTools`, `AgentResult.ReportFile`, the sandbox client's `copilot` argument, Claude Code's tool names in
the engine, each runtime's second hook table, `api.base_url`, and the manifest's unused `from`, `os` and `docker`.

## Deviations and judgement calls

LOG 440 to 451, unreviewed. Three are flagged for the product owner: LOG 441 (a repository image works only once the
operator lists it in `allowed_images`, because the agent can edit the manifest on its branch and a Docker sandbox's
network is closed inside the image), LOG 442 (`environment` drops the `devcontainer.json` reference of `docs/10` Q3),
and LOG 444 (the engine's hook answer carries the runtime's reply; the client knows no runtime). The model proxy now
needs `model_access` on every runtime instance that uses it: an installation whose second runtime relied on the
default's credential must give it its own `key_ref` (LOG 446; no backward compatibility, LOG 263).

## Follow-ups

| # | Follow-up |
|---|---|
| 1 | No Kubernetes run with a repository image: the Kubernetes sandbox's refusal case is in the `Cluster` category, not run for A5 (M8 follow-up 28) |
| 2 | A repository image that cannot be pulled is retried as an infrastructure fault rather than stopping for a person (M8 follow-up 28) |
| 3 | M8 follow-up 14, unowned: the Copilot-quota live trial (a proxy upstream for Copilot's backend, or a Copilot-scoped token) and GitHub's terms; the proxy records but does not pin each request's model |
| 4 | M8 follow-up 15, unowned: a live check of Copilot CLI with the engine unreachable (its reply is exit 2, which blocks each call but does not stop the session); skills or agents the agent adds to Copilot's sticky `COPILOT_HOME` |
| 5 | The runtime's unreachable reply applies on `pre_tool_use` only; other hooks exit 0 and the session-stop marker ends the session as resumable (LOG 444) |
| 6 | Copilot's hook table no longer allows the Claude-only names (`LS`, `TodoWrite`, `MultiEdit`, `NotebookEdit`) that `HookPolicy` allowed for any runtime; Copilot does not offer them (LOG 445) |
| 7 | `tools/check_m4_controls.py` was retargeted but not run (about forty minutes) |
| 8 | Agent integration tests time out or lose their container when other Integration suites share the machine's Docker and CPU (A2 follow-up 7 saw the same); they pass alone. A full Integration run on an otherwise idle machine, or on CI with `full-ci`, is the check before merge |
