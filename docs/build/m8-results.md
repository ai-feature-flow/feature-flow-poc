# M8 results: team profile and operations

## Foundations

What M8's parallel builders start from, built on `m8-foundations` (W0) before them: the backward-compatibility code
removed, the organisation keys not in version 1 refused, and the shared files the builders would otherwise all edit
prepared. No provider implementations.

**What was built**

| Area | Change |
|---|---|
| No backward compatibility (LOG 263) | All 21 Temporal patch markers in `RunWorkflow*.cs` deleted with the old-history branches they guarded; today's behaviour kept. The interpreter's `RunOptions.BranchFromPattern` and `Steering`, which the shell set from markers, deleted with their old paths: the pre-M7 skip refusal (LOG 207), the pre-M7 `auto_resolved` text (LOG 258), and the pre-M7 patch and go-to checks. `artifact.validate`'s path for a scope without consumed artifacts deleted (LOG 272). `project_session_pre_m7` (LOG 240's pre-session path) and the tests of old paths deleted |
| Histories | Every other history broke on replay (a marker is a recorded command) and was re-recorded from the integration suite with `FF_RECORD_HISTORIES`. The marker-presence tests became one table: each history holds the activity, signal, update or recorded text of the path it covers (LOG 271) |
| Organisation keys (LOG 268 to 270) | `library_overrides`, `library_inputs`, `timeouts` and `policy.toolsets` refused whole: "`<key>` is not supported in version 1; remove it". Their parsing and model fields removed (LOG 273). `role_mapping` stays refused until the Agents builder lifts it with its consumer (LOG 267) |
| Store contract suites | `RecordStoreContract` (runs, waits, timeline, answers, locks, agent step records) and `ArtifactStoreContract` (content addressing, round trips, `not_found`, signed URLs), subclassed for SQLite and the filesystem store (LOG 274) |
| Provider scaffolding | New `FeatureFlow.Providers.S3` and its tests project. Postgres, S3, Kubernetes, CopilotCli and Jira each have an empty `Factories` list, registered by all three hosts (LOG 275) |

**Checks**

| Check | Shows |
|---|---|
| Replay of 19 re-recorded histories | Today's workflow replays every recorded path |
| Each history takes the path it covers (14 rows) | A change to any covered path fails a replay rather than passing unseen |
| Each key not in version 1 is refused alone, whatever it holds | The four refusals, with malformed content still refused only for the key |
| `RecordStoreContract`, `ArtifactStoreContract` on SQLite and the filesystem | The solo profile's stores meet the suites the Postgres and S3 providers must pass |
| Architecture test | The S3 project is in the reference rule table and obeys it |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Replay | The workflow skips the `clean_up` activity: `ticket_clean_up` fails with a nondeterminism error |
| Path table | Run against the old `family_parent` (recorded before `EnsureWorkBranches`): its row fails |
| Key refusal | The refusal's error removed: the test fails |
| Record store suite | Timeline `INSERT OR REPLACE`; a late "open" replaces an answered question; runs listed oldest first: the three matching tests fail |
| Artifact store suite | A put truncated at 1 MB; a signed URL to the store's root: the two matching tests fail |
| Architecture test | S3 dropped from the rule table, and Jira pointed at `Engine`: it fails naming both |

**Deviations and judgement calls:** LOG 271 to 275.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | No sandbox or agent runtime contract suite exists; the Docker sandbox is covered only by the agent integration tests. The Agents builder writes `SandboxContract` (with the network checks) once, for Docker and Kubernetes |
| 2 | `docs/02` section 8's provider decorator (spans, timeouts, classification) does not exist; the Operations builder adds it in `ProviderComposer` |
| 3 | Library sources are resolved to a directory in three places (`ApiApp.Runs`, `ApiApp.Decisions`, `StartupChecks`); the `git` source needs one resolver used by all three |
| 4 | `ChildRunTests`' artifact-index order raced once in the full integration runs (it passed 5 of 5 alone). The workflow starts a child before it records the step that produced the parent's artifact, so under load the child's records can land first. New runs took the same order before M8. Fix in the workflow (record before starting children, re-recording the histories that breaks), not by loosening the assertion |

## Agents

The sandbox's network (M4 follow-up 12), the Kubernetes pod sandbox, the Copilot CLI runtime and `role_mapping`, built on
`m8-agents` (W2).

**What was built**

| Area | Change |
|---|---|
| Sandbox network | A sandbox reaches the agent worker (`agent_worker.urls` and the model proxy) and the internet, nothing else on its host or cluster. Docker: root's iptables rules inside the container at creation, the agent with no capability to change them (LOG 291). Kubernetes: `sandbox-network-policy.yaml`, checked present at startup (LOG 294) |
| `SandboxContract` | New contract suite: sandboxes found by key and gone when disposed; commands as the agent's user, polled, re-attached, cancelled, unforgeable; copies; read-only paths; the engine's home; and the network checks. Run on Docker (Integration) and Kubernetes (new category `Cluster`, LOG 297) |
| `ShellSandbox` | The command machinery both sandboxes share, in `Providers.Abstractions` (LOG 292). It fixed `CancelCommandAsync`, which had never signalled |
| Kubernetes pod sandbox | `KubernetesSandbox` (KubernetesClient): a pod per sandbox, CPU and memory limits, no service account token, `/ff` an `emptyDir` (LOG 294) |
| Copilot CLI runtime | `CopilotCliRuntime`, from Spike 2's provider on Claude Code's pattern. The model through the one proxy as a custom Anthropic provider with the step's token; settings and hooks the agent cannot change; reports through the engine's MCP tools (LOG 293, 296). `HookPolicy` reads Copilot's names; the hook command answers Copilot with a JSON decision |
| `role_mapping` | The refusal is lifted. Resolved into the run's policy at start; each step runs on its role's runtime instance and model, recorded on the run (LOG 295) |
| Image | `feature-flow-sandbox:m8`: iptables, tini, curl and Copilot CLI 1.0.105 added |

**Checks**

| Check | Shows |
|---|---|
| `SandboxContract` on Docker, 12 tests | The contract, and that the sandbox reaches the worker's port and github.com, but not another port of the host, a service on the host's loopback, or a neighbouring container; nor after the agent tries to flush the rules |
| `SandboxContract` on k3d, 13 tests | The same for a pod: the stand-in agent worker in another namespace and github.com reached; a stand-in store, the Kubernetes API, the node and the Docker host not. The provider refuses to start where the policy is absent |
| Agent integration tests (25), with the network closed | The worker, hooks and proxy still reach every M4 path |
| `RoleMappingTests` | An implementer runs on `claude_code` with `claude-haiku-4-5`, a reviewer on a second instance with that instance's own Opus model, read from the requests the scripted model received |
| `CopilotStepTests`, 4 tests | The real Copilot CLI in the sandbox, scripted model behind the real proxy: a step commits, is denied `git push` with the reason, reports through `engine-report`; SP5 C2 to C4 (no model or GitHub credential in any process environment, the home or the transcript), N1 (model unreachable: stops resumably, then completes), N2 (key refused: stops for a person) |
| `LiveCopilotTests`, 4 tests | SP5 for Copilot CLI with the real model, which SP5 did not test: C1 a real step committed to the fixture's work branch; C2 to C4 held, and Copilot keeps even its provider key out of its tools' shells; N1 two sessions ended `model_unreachable`, no attempt used, nothing reported; N2 a 401 stopped the step for a person. No reference left on the fixture |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Docker network | No rules at creation: the host's other port is reached (2 checks fail). No allow entry: the worker is not reached. The agent run as root: it flushes the rules |
| Kubernetes network | The policy selecting no pods: the store is reached. The agent-worker rule removed, or without `namespaceSelector`: the worker is not reached. An unlabelled pod reaches all four forbidden endpoints, so each forbidden check can fail |
| Cancel | The old `kill -TERM --` line: the cancel check fails |
| `role_mapping` | The workflow ignoring the mapping, the worker ignoring the step's instance, Claude Code ignoring the task's model: each fails `RoleMappingTests` |
| Copilot hooks | No `PreToolUse` hook: the push is attempted. The exit-2 denial for Copilot: the agent sees no reason. `HookPolicy` without `engine-*` or `path`: 4 tests fail |

**Deviations and judgement calls:** LOG 291 to 297.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | One model proxy serves every runtime (LOG 293). A runtime on a different upstream (an OpenAI-compatible router) needs a proxy per runtime instance and a second wire format |
| 2 | Copilot CLI's handling of the hook's `continue: false` when the engine is unreachable is untested: the MCP path ends the runtime itself, the hook path may let the call through (as "unreachable is not denied" requires) without stopping the session |
| 3 | The agent can add its own files (skills, agents) to Copilot's sticky home between sessions of a step. They run in the same sandbox with the same rights; only `settings.json` and `mcp-config.json` are protected |
| 4 | `CopilotCliRuntime` repeats some of `ClaudeCodeRuntime` (the `FF_` environment, the result's marker and report file); a shared helper would need a home in `Providers.Abstractions` |
