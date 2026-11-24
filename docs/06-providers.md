# Providers

Status: draft, 2026-10-31.

This document owns the provider interfaces: the rules every provider follows,
error classification, and each interface's capabilities and methods. It merges
the first design's interfaces and contracts (`archive/docs/04`, `05`), with the
changes listed in section 2. Which steps call which provider is in `docs/04`;
how providers are selected and configured is in `docs/07`.

Signatures are C#, abbreviated: every method is async, takes a
`CancellationToken` (omitted below) and returns `ProviderResult<T>`.

---

## 1. Rules every provider follows

| Rule | Detail |
|---|---|
| Base interface | `Describe()` returns type, name, version and the typed capability record. Each provider's factory lists the settings it accepts; any other key is refused when the providers are composed. `CheckAsync()` is called at startup and run start |
| Capabilities | Typed records, never strings, holding only the flags the engine reads; a type with none describes `NoCapabilities`. A run checks every capability its plan needs when it starts (P9, check 21), on the instance that serves each step: an agent step's runtime is the one its role maps to (`role_mapping`), a dry run's the `dry_run` instance. Flags are not read again before a call. The flags: `agent_runtime.hooks` and `agent_runtime.mcp`, which every agent step needs, and `code_host.pull_request` and `code_host.check_status`, which actions need (`docs/12` R1, F16) |
| Short and bounded | No method waits on human or external time. Long work is started and polled; waiting is the engine's (`docs/02` section 2.3) |
| Classified results | Expected failures are `ProviderError` values with a class and a reason (section 1.1). Exceptions are programming errors. The engine's decorator records one on the call's span and re-throws it, so the caller decides: an engine action stops for a person (an intervention), and only network faults are retried (LOG 303) |
| Every success mapped | Every documented success response of the remote system maps to a result, including empty ones such as GitHub's 204 for a merge with nothing to merge. An unmapped success is `unexpected_response`, a **work** error, so it is never retried forever (Spike 3, `archive/docs/18` section 4) |
| Idempotent creates | A create method takes an idempotency key and writes a hidden marker into the object. On a retry the provider finds an object carrying the key's marker and adopts it rather than create another, because a timed-out create may already have happened |
| Credentials | From the credential broker (section 3.13) where one serves the system (GitHub), else from the secrets provider by a `*_ref` setting. No configuration holds a credential's value (F2) |
| Transport-neutral | Plain serialisable records only; no third-party SDK types. A remote provider adapter (gRPC or HTTP, for providers in other languages) stays possible; it is not built in version 1 |
| Observability | The engine's decorator adds spans, timeouts and classification; providers add attributes only |
| Contract tests | One suite per interface, except secrets and the credential broker, shipped with the engine. It includes every documented success response. A provider conforms only if it passes |

### 1.1 Error classification

| Reason | Class | Retry | Meaning |
|---|---|---|---|
| `unreachable`, `server_error`, `internal_error` | Infrastructure | Yes, with backoff | The remote or the network failed; the work did not happen |
| `timeout` | Infrastructure | Yes | The work **may** have happened: a create adopts the object carrying its marker |
| `rate_limited` | Infrastructure | Yes, honouring `retry_after` | — |
| `unauthenticated`, `forbidden` | Infrastructure | Once, by the provider, after about 2 s, with a freshly issued credential; then the engine stops for a person: an action, a `clean_up` or the agent step raises an intervention naming the configuration fault, and run start refuses the start (LOG 147). What the person is told to check comes from the provider that failed: the GitHub providers name the App's key, installation and permissions, Jira its secret, Slack its bot token; a provider that gives no hint gets a neutral one naming no system (L10, LOG 451) | Almost always configuration |
| `not_found`, `conflict`, `validation_failed`, `rejected` | Work | No | The remote answered; the step maps it to an outcome or an intervention. A permission the provider's identity lacks (GitHub's 403 "Resource not accessible by integration") is `rejected`, named a configuration fault: it does not heal, so it stops for a person at once (LOG 119) |
| `base_moved` | Work | No | A push was refused because the target moved since it was read |
| `capability_missing` | Work | No | — |
| `unexpected_response` | Work | No | An unmapped success |
| `partial` | Work | The step decides | Some of the work happened |

Infrastructure retries never use a step's attempts (`docs/01` section 4). A
step may reclassify a provider's error because it knows the intent (`docs/04`
section 1).

### 1.2 Default timeouts

The engine's decorator sets a timeout per call by provider type (LOG 304):

| Provider types | Timeout |
|---|---|
| Tracker, code host, notifications, findings source, secrets, credential broker, record store | 30 s |
| VCS, CI, artifact store | 5 min |
| Sandbox, agent runtime, session surface, validator | None: the step or heartbeats bound them |

---

## 2. Changes from the first design

| Change | Why |
|---|---|
| The review provider is replaced by a **findings source** provider: an external reviewer that returns findings (an AI review on a pull request). The standard reviewer is an agent step (RW5) | One way to run an agent |
| New **validator** provider | The plug point for deterministic cross-artifact checks (RW7) |
| New **toolset** provider | External MCP servers for agents, proxied by the engine (from FF). Not in version 1 (section 5) |
| CI provider gains `HasCiAsync` | `ci.checks` must tell "no CI" from "not started" without GitHub-specific logic in the step (`docs/04` section 13). It replaced `GetExpectedRunsAsync`, which predicted the runs from workflow triggers (LOG 147) |
| Code host loses `CreateBranchAsync` | Work branches are created by a VCS push (`docs/04` section 7) |
| Tracker statuses are mapped from the library's status names in provider configuration | Templates name statuses abstractly (`docs/03` section 2.5) |

---

## 3. The providers

| # | Provider | Used by |
|---|---|---|
| 3.1 | Sandbox | Agent steps, `test.run`, `test.integration`, `analysis.run` |
| 3.2 | Agent runtime | Agent steps |
| 3.3 | VCS | The engine's pushes, catch-up and merges |
| 3.4 | Code host | Pull requests, checks, protection, comments |
| 3.5 | CI | Tiers delegated to CI, `ci.checks` |
| 3.6 | Tracker | `tracker.update`, declarative tracker updates |
| 3.7 | Findings source | `findings.record` |
| 3.8 | Validator | `artifact.validate` |
| 3.9 | Toolset | Agent steps, through the engine MCP server |
| 3.10 | Notifications | Decisions and stuck runs |
| 3.11 | Session surface | Interactive sessions |
| 3.12 | Artifact store, record store, secrets | The engine itself |
| 3.13 | Credential broker | Every provider |

Temporal and the worker platform are configuration only (`docs/07`).

A `fake` provider of any type but the stores and secrets only describes itself. Neither it nor a `memory`
provider is configured in a production installation: an installation that
configures one must set `development: true`, as must one that configures the
`scripted` agent runtime (L4). An action that no handler or configured provider
can serve stops for a person (`docs/04` section 1).

The VCS, code host and CI interfaces as built in M5 differ from the signatures below: catch-up and merge take an
expected revision and a key, the code host reads a file and has no merge method, and CI results are returned as
unparsed files. LOG 107 lists them.
How the Git, GitHub and GitHub Actions providers meet them, and the engine's new `actions: write`, are LOG 108
to 115.

The tracker, findings source and validator interfaces as built in M6, the code host's comments, and the findings
ledger and artifact index in the record store differ from the signatures below: LOG 148 to 151. How the GitHub
Issues tracker maps statuses, parents and links is LOG 184; the Jira tracker, LOG 301 and 302. The engine's decorator
(spans, timeouts, classification) is LOG 303 and 304.

The notifications and session surface interfaces as built in M7, and the records M7 adds to the record store
(overrides, notification de-duplication, sessions and their checks), differ from the signatures below: LOG 201 and 202.

### 3.1 Sandbox

```csharp
record NoCapabilities;   // the sandbox declares no flags: nothing reads them (F16)

record SandboxSpec(string Key, string Home, IReadOnlyDictionary<string, string> Environment,
                   IReadOnlyList<string> Folders, string? Image = null);

interface ISandboxProvider : IProvider {
    Task<..SandboxHandle> CreateAsync(SandboxSpec spec);
    Task<..SandboxHandle> AttachAsync(string key);
    Task<..CommandHandle> StartCommandAsync(SandboxHandle s, SandboxCommand command, string key);
    Task<..CommandHandle> AttachCommandAsync(SandboxHandle s, string key);
    Task<..CommandStatus> GetCommandStatusAsync(CommandHandle c, long cursor);
    Task<..Unit>          CancelCommandAsync(CommandHandle c);
    Task<..Unit>          CopyInAsync(SandboxHandle s, ArtifactRef source, string path, bool readOnly);
    Task<..ArtifactRef>   CopyOutAsync(SandboxHandle s, string path);
    Task<..Unit>          MakeReadOnlyAsync(SandboxHandle s, string path);
    Task<..Unit>          DisposeAsync(SandboxHandle s);
}
```

Signatures here and in sections 3.2, 3.3 and 3.13 are those built in M4 (LOG 103),
abbreviated: every method also takes a cancellation token, and `..X` stands for
`ProviderResult<X>`.

- `SandboxSpec` gives the step's key, the engine-controlled home (DR19),
  environment and folders. The checkout is the VCS provider's (`CheckOutAsync`,
  section 3.3), not the sandbox's.
- `SandboxSpec.Image` is the repository's `environment.image` (`docs/07`
  section 4), or null for the sandbox's own `image` (LOG 440). The agent step
  passes its work branch manifest's image; `test.run` and `analysis.run` pass the
  parent branch's, as they do its `setup`, because both shape the evidence. The
  sandbox never reads the manifest itself. A container sandbox uses the image
  only when it is its own or matches one of its `allowed_images`, and refuses any
  other as `cannot_answer`, which stops the step for a person (LOG 441).
- After a worker restart the agent worker re-attaches a step's sandbox by its
  key (`AttachAsync`); one that cannot be found, or whose preparation did not
  finish, is made again, as for a lost sandbox. No capability flag declares this
  (F16).
- Read-only checkouts are enforced by the sandbox (`MakeReadOnlyAsync`), not the
  agent.
- Commands are started and polled; `AttachCommandAsync` re-attaches to a running
  command after a worker restart (Spike 1, `archive/docs/15` section 3.4).
- Every sandbox has the engine's hook client and MCP endpoint configured, with
  the per-step token (`docs/02` section 4.2).
- Network policy, CPU and memory limits are applied by every built sandbox; no
  capability flag declares them (F16). FF's containers had none (`../feature-factory/issues.md`).
- Built in M8: a sandbox reaches the agent worker (its `urls` and model proxy)
  and the internet, never Temporal, the stores or anything else on its host or
  cluster. Docker closes it with rules inside the container (LOG 291); the
  Kubernetes pod sandbox with a network policy (LOG 294). Both share one command
  machinery (LOG 292) and pass `SandboxContract`, which checks the network.

**The base sandbox image.** The engine ships one base image, `src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile`
(Debian with Node, Claude Code and Copilot CLI 1.0.107, pinned; LOG 497). An installation's sandbox `image` is it or an image built
on it. A repository that needs a toolchain (a .NET SDK, a JDK) names its own image in `environment.image`,
built `FROM` the installation's image, and the installation lists it in `allowed_images` (LOG 441). The
engine neither builds nor pushes images: Docker pulls an absent image at creation, a Kubernetes node as it
schedules the pod. What the engine requires of an image (LOG 443):

| Requirement | Why |
|---|---|
| Linux on glibc, with `sh`, coreutils, `tar`, `awk`, `getent`, `setsid` and `setpriv` (util-linux), and `git` | The shared command machinery (LOG 292), the self-contained sandbox client (LOG 67) and the Git provider's checkout run on them |
| The agent's user (the sandbox's `user` setting, default `node`, uid 1000), unprivileged; root reachable by the engine's exec | Commands run as the agent's user under `setpriv`; copies, read-only paths and the network rules are root's |
| `iptables` and `ip6tables` (Docker), `tini` (Kubernetes) | Docker closes the network from inside the container, and a sandbox without them is removed at creation; the pod runs `tini -- sleep infinity` |
| `claude` and `copilot` on `PATH`, at versions the runtimes support | Only for agent steps run by those runtimes; tests and analysis do not use them |
| No `ENTRYPOINT` that does not run its arguments, and nothing at start that needs the network | Docker starts the container as `sleep infinity` with its own init; the network is closed before anything else runs |
| Nothing relied on in `/ff`, the home directory, `USER`, `WORKDIR` or `ENV HOME` | The engine creates `/ff` and the home per step (DR19), sets `HOME`, and runs each command as a named user in a named folder |

A repository image adds to the base with root (`RUN apt-get install …`) and removes nothing from it. At
run time its sandbox has the same network as any other: the agent worker and the internet (LOG 291, 294).

### 3.2 Agent runtime

```csharp
record AgentRuntimeCapabilities(bool Hooks, bool Mcp);   // every agent step needs both, of the runtime its role maps to

interface IAgentRuntimeProvider : IProvider {
    Task<..AgentSession>  StartAsync(SandboxHandle s, AgentTask task, AgentLimits limits);
    Task<..AgentSession>  AttachAsync(SandboxHandle s, AgentTask task);   // a running session, by the step's key
    Task<..AgentSession>  ResumeAsync(SandboxHandle s, string sessionRef);
    Task<..AgentSession>  ContinueAsync(SandboxHandle s, AgentTask task, AgentLimits limits, string message);
    Task<..AgentProgress> GetProgressAsync(AgentSession session);   // finished, turns
    Task<..AgentResult>   GetResultAsync(AgentSession session);     // how it ended, usage, file report
    Task<..Unit>          StopAsync(AgentSession session);
}
```

- `AgentTask` carries the rendered task, the reporting preamble, tool allow-list,
  skills and resources paths, the MCP endpoint, model and limits.
- The `report` tool's record is the source of truth for the agent's report
  (`docs/04` section 3.3); `GetResultAsync` gives how the session ended and its
  usage. A session-end hook only wakes the engine. There is no file report
  (L5, LOG 448).
- `ContinueAsync` continues an ended session with a message: a rejected
  report's errors, or word that the attempt was interrupted.
- `ResumeAsync` reloads the conversation from the recorded reference and must
  work in a fresh worker process.
- Files opened are recorded from hooks, or from streamed tool calls, or reported
  as unknown. A workflow may require coverage of changed files; the engine only
  records it.
- Measured (`archive/docs/16` sections 4 and 12): Claude Code supports hooks on
  Windows and macOS but only the Anthropic API; Copilot CLI works through an
  OpenAI-compatible router but its tool-call hooks did not fire with a
  third-party model.
- Built in M8: the Copilot CLI runtime, reaching the model through the model
  proxy, with SP5's checks held live (LOG 293, 296). Each step's runtime
  instance and model follow its role (`role_mapping`, LOG 295). How the CLI
  reaches the proxy follows its instance's `model_access.auth` (LOG 496, 497):
  - `github`, Copilot quota: the CLI runs in its native GitHub mode.
    `COPILOT_API_URL` (read by the CLI, not documented) points its Copilot API
    at the proxy, and `COPILOT_GITHUB_TOKEN` holds `ghs_` and the step's token;
    there is no `COPILOT_OFFLINE` and no `COPILOT_PROVIDER_*`. The CLI makes its
    own requests, so GitHub's billing headers are its own. Its few calls to
    `api.github.com` carry only the step's token, which GitHub refuses, and do
    not stop the session; nor does its `GET /agents/tasks/<session id>`, which
    reaches the agent endpoint outside `/model/` and is answered 404 (both
    accepted, LOG 496). Models are named as in Copilot's catalogue
    (`claude-haiku-4.5`), and must offer `/v1/messages` or `/chat/completions`:
    `/responses` is not forwarded.
  - `api_key` or `oauth`: the proxy is a custom Anthropic provider, offline,
    with the step's token as its key (LOG 293).
- Copilot's home (`COPILOT_HOME`) belongs to one step: created at its first
  session, root's and sticky (1777) with the engine's settings read-only. A
  continuation keeps only what `--resume` reads (`session-state/`,
  `session-store.db*`, `config.json`), so nothing else the agent adds, such as
  a `settings.local.json` or a skill, reaches a later start-up; the home is
  removed when the step ends, so nothing outlives the step (M8 follow-up 15,
  LOG 497). The base image pins Copilot CLI 1.0.107, the version the native
  mode was shown on (spike 0); the Copilot Live tests re-check any bump
  (LOG 497, 498).
- **A runtime whose CLI runs in a sandbox** derives from
  `SandboxedAgentRuntime` (F12, A5), which holds the session lifecycle: a
  session is the CLI started by key in the step's sandbox from settings files
  the engine writes read-only, found again by that key, polled for its activity
  as its output grows, read
  when it ends (the engine-unreachable marker, an API error by its status, a
  lost process, the exit code) and stopped by cancelling it. A runtime supplies
  its command line, environment, settings files, transcript parser, output format,
  the tools it offers for each tool class with the other names its hooks report
  for them (one table), how its CLI is told a call is denied, and how it is
  stopped when the engine is unreachable.
- **One reader for a runtime's output** (`AgentTranscripts`, LOG 544) reads
  Copilot CLI's JSON-lines session events and Claude Code's stream-json, for
  the API's readable transcript (whole text, as turns) and for live activity (fed
  as the output grows). Each poll, the runtime feeds the output written after its
  cursor; the reader keeps a partial last line for the next chunk and returns the
  activity entries the complete lines finished, capped (`docs/04` section 3.1),
  as `AgentProgress.Activity`. Turns and entries come from the same code for each
  event, so the two views cannot drift; streamed pieces make no entry.
  `AgentProgress.Turns` counts the agent's messages. Every attach, by a new
  worker or by the same one on a retried attempt, registers a fresh live reader
  at the start of the output, so the entries are numbered as before and two
  attempts never share a reader. The live reader keeps no turns and only the tail
  of a running shell's output; a session's reader is dropped when its result is
  collected or it is stopped.
- **Hooks are read by the runtime** (`IAgentHooks`, LOG 444). Every runtime's
  settings call the same hook command, `ff-sandbox hook <event>`. The step's
  token names its runtime instance; the agent worker asks that instance to read
  the hook input as a tool call (its class: `read`, `search`, `edit`, `shell`,
  or `engine` for the engine's MCP tools; the file; the command), and the hook
  policy decides on classes alone. A denial carries the runtime's reply: Claude
  Code exits 2 with the reason on standard error; Copilot CLI, which shows the
  agent only the exit code of a hook that exits 2, prints its JSON decision with
  the reason and exits 0. A tool the runtime does not map is denied; a call with
  no decision (a revoked token) is blocked with exit 2. When no replica answers
  for the hold (R19), the hook command marks the session stopped and, on the
  pre-tool-use hook, applies the reply the runtime gave it in its environment
  (`FF_HOOK_UNREACHABLE`): Claude Code's `continue: false`, which stops the
  session, or exit 2 for Copilot CLI, which blocks the call. With no reply it
  blocks the call with exit 2, so the hook fails closed for any runtime.
- **The agent runtime contract** (`AgentRuntimeContract`, LOG 447) drives a
  runtime against a scripted sandbox and a stand-in for its CLI, with no model,
  credential or container. The stand-in reads the settings and command line the
  runtime wrote as the CLI does, prints the CLI's output, and sends its hook
  input. The suite checks: settings written read-only, calling the sandbox
  client for every hook, with DR16's timeouts; the step's token and no
  credential in the environment, and the task only in the prompt file; only the
  role's tool classes offered; every tool name the CLI's hooks can report, offered
  or not, read as the class of the CLI's tool, one tool at a time, by the
  stand-in's own table; a denial the CLI blocks with the reason shown; the
  engine-unreachable reply, which the CLI blocks; and the lifecycle (start,
  turns, result, continuation of the same session, re-attach by a new worker,
  stop, and each way a session ends). Claude Code and Copilot CLI pass it; any
  tool moved to the wrong class or left unmapped fails it (LOG 447). Integration
  tests run the real CLIs against a scripted model.

### 3.3 VCS

```csharp
record NoCapabilities;   // VCS declares no flags (F16)

interface IVcsProvider : IProvider {
    // A working copy in a sandbox, reached through the sandbox provider
    Task<..Unit>         CheckOutAsync(RepoRef repo, string sourceReference, string revision,
                                       string branch, WorkingCopyRef target);   // a bundle, no remote
    Task<..string>       GetRevisionAsync(WorkingCopyRef copy);
    Task<..bool>         IsDirtyAsync(WorkingCopyRef copy);
    Task<..string?>      CommitAllAsync(WorkingCopyRef copy, string message);
    Task<..CommitBundle> FetchCommitsAsync(WorkingCopyRef copy, string sinceRevision);
    Task<..int>          CountCommitsAsync(WorkingCopyRef copy, string fromRevision, string toRevision);
    Task<..IReadOnlyList<string>> ChangedFilesAsync(WorkingCopyRef copy, string fromRevision, string toRevision);
    Task<..IReadOnlyList<string>> FilesAtAsync(WorkingCopyRef copy, string revision, IReadOnlyList<string> paths);   // those that are files there
    // The remote, through the engine's own clones (docs/02 section 6)
    Task<..string?>      ResolveAsync(RepoRef repo, string reference);   // null when absent
    Task<..bool>         DescendsFromAsync(RepoRef repo, string revision, string ancestor);
    Task<..string?>      MergeBaseAsync(RepoRef repo, string a, string b);
    Task<..PushResult>   PushAsync(RepoRef repo, string reference, string revision,
                                   CommitBundle? commits, string? expectedOldRevision);
    // M5
    Task<..CatchUpResult> CatchUpAsync(RepoRef repo, string parent, string work,
                                       string expectedWorkRevision, string message);
    Task<..MergeResult>  MergeAsync(RepoRef repo, string testedRevision, string target,
                                    string expectedTargetRevision, bool squash, string message);
}
```

- Version 1 implements Git only; the interface speaks of revisions and
  references, not Git types.
- `PushAsync` and `MergeAsync` refuse with `base_moved` if the target is not
  the expected revision. That refusal is what makes the lock-free merge safe.
- Agents never call this provider.

### 3.4 Code host

```csharp
record CodeHostCapabilities(bool PullRequest, bool CheckStatus);   // read from the action descriptors (check 21)

interface ICodeHostProvider : IProvider {
    Task<..string>           GetDefaultBranchAsync(RepoRef repo);
    Task<..BranchProtection> GetBranchProtectionAsync(RepoRef repo, string branch);
    Task<..RepoFile?>        ReadFileAsync(RepoRef repo, string path, string reference);
    Task<..PullRequestRef>   OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key);
    Task<..PullRequestRef>   UpdatePullRequestAsync(PullRequestRef pr, PullRequestSpec spec);
    Task<..PullRequestState> GetPullRequestAsync(PullRequestRef pr);   // state, head, approvals, merged by whom, size
    Task<..IReadOnlyList<PullRequestFile>> ListPullRequestFilesAsync(PullRequestRef pr);   // path, status, +/-
    Task<..Unit>             ClosePullRequestAsync(PullRequestRef pr, string? comment);
    Task<..PullRequestRef>   CreateRevertPullRequestAsync(PullRequestRef pr, string key);
    Task<..CheckStatus>      GetCheckStatusAsync(RepoRef repo, string revision);
    Task<..CommentRef>       AddCommentAsync(PullRequestRef pr, string body, string key);
}
```

- The default branch is always read from here, never from configuration or the
  plan (F1, F10).
- `GetBranchProtectionAsync` only checks protection exists; the engine's
  identity has no permission to change it (F5).
- `GetPullRequestAsync` reports who merged, so `scm.merge` can detect a person's
  merge on the code host, and the pull request's size (additions, deletions,
  changed files) and mergeable state where the host gives them.
  `ListPullRequestFilesAsync` lists its files; GitHub gives the first 300
  (`GET /pulls/{n}/files`, three pages). The engine records both on each read
  of a run's pull request (`docs/04` section 7.2, LOG 531).

### 3.5 CI

```csharp
record NoCapabilities;   // CI declares no flags (F16)

interface ICiProvider : IProvider {
    Task<..CiRunRef>    TriggerAsync(RepoRef repo, CiJobRef job, string commit);
    Task<..CiRunStatus> GetRunStatusAsync(CiRunRef run);
    Task<..CiRunResult> ReadResultsAsync(CiRunRef run);    // parsed test results where available
    Task<..LogRef>      ReadLogsAsync(CiRunRef run);
    Task<..bool>        HasCiAsync(RepoRef repo, string head);   // any CI configured at this head
}
```

- Tiers that need Docker or Windows run in CI by default, so this provider is on
  the main path. Spike 2 dispatched such a tier and polled it to green
  (`archive/docs/16` section 2).
- A trigger is not idempotent. The dispatch returns the run's id, which the
  engine records as the step's own event, so the activity's retries poll that
  run. A trigger retried after a lost answer dispatches again; the rare
  duplicate run is accepted (LOG 147).
- The record's time is the run's dispatch time (`CiRunRef.DispatchedAt`).
  GitHub's run lookup can answer 404 for a few seconds after the dispatch
  returned the id, so the GitHub Actions provider reports a run it reads 404
  for as `queued` until `DispatchGrace` (5 minutes) after its dispatch, and
  only then as `not_found`, a work failure. The step keeps polling meanwhile, as
  for any queued run; a run with no dispatch time gets no grace (LOG 500).
- `HasCiAsync` answers only "is CI configured for this head" (for GitHub
  Actions, a workflow file exists), so `ci.checks` knows to wait a while for a
  first check. Which runs must pass comes from the target branch's required
  checks, not from evaluating workflow triggers (LOG 147).

**The CI workflow contract (GitHub Actions; LOG 112, 119).** The provider dispatches a tier's job (its workflow file, from
the manifest's `ci.job`) on the repository's **default branch**, so the workflow definition and what it does with the
repository's secrets are reviewed code, never a file the agent edited. A workflow used as a CI job must:

- be triggered by `workflow_dispatch` and declare an input `commit_sha`; the provider sets it to the revision under
  test and refuses a workflow without it;
- check that revision out (`actions/checkout` with `ref: ${{ inputs.commit_sha }}`) and build and test only it,
  because the run's own `head_sha` is the default branch's and its results are filed under `commit_sha`;
- upload its result files (`.trx`, JUnit `.xml`, `.sarif`) as artifacts under 100 MB, the provider's one cap on
  downloads (logs and each decompressed result file included; LOG 147);
- set `permissions: contents: read`, so the code under test cannot write to the repository through `GITHUB_TOKEN`;
- if it is also triggered by `push` on the branches agents write to (`ff/**`), use no secrets. A push runs the
  workflow file as the pushed commit has it, which the agent may have edited, so any secret that run can reach is
  exposed to the agent's code. Keep secrets for jobs that run only from the default branch (LOG 119).

### 3.6 Tracker

```csharp
record NoCapabilities;   // the tracker declares no flags (F16)

interface ITrackerProvider : IProvider {
    Task<..Issue>                GetIssueAsync(string key);
    Task<..IReadOnlyList<Issue>> SearchAsync(IssueQuery query);   // by label, parent, marker
    Task<..IssueRef>             CreateAsync(IssueSpec spec, string key);
    Task<..IssueRef>             UpdateAsync(IssueRef issue, IssueSpec spec);
    Task<..Unit>                 TransitionAsync(IssueRef issue, string status);   // a library status name
    Task<..Unit>                 LinkAsync(IssueRef from, IssueRef to, string linkType);
    Task<..Unit>                 CommentAsync(IssueRef issue, string body);
}
```

- Field and status mapping are provider configuration: a library status such as
  `code_review` maps to the tracker's own status name.
- Transitioning to the current status succeeds.

### 3.7 Findings source

```csharp
record NoCapabilities;   // a findings source declares no flags (F16)

interface IFindingsSourceProvider : IProvider {
    Task<..CollectHandle>  RequestAsync(FindingsTarget target, string key);   // e.g. ask for a review of a head
    Task<..CollectStatus>  GetStatusAsync(CollectHandle handle);              // pending, ready, unavailable
    Task<..ArtifactRef>    ReadFindingsAsync(CollectHandle handle);           // a findings report artifact
}
```

- Version 1 has one: Copilot review on a pull request (composed with the code
  host provider).
- The result is a findings report in the same type an agent reviewer produces,
  so `findings.record` treats both alike.
- A source that cannot review (not enabled, ignored the request) reports
  `unavailable`, which `findings.record` returns as an outcome, so a repository
  without it does not stop a run (from FF).

### 3.8 Validator

```csharp
interface IValidatorProvider : IProvider {
    IReadOnlyList<ValidatorDescriptor> Validators();   // name, subject types, options schema, offered reports
    Task<..ValidationResult> ValidateAsync(string validator, IReadOnlyList<ArtifactRef> subjects,
                                           IReadOnlyDictionary<string, object> options,
                                           FrozenRepositories repos);
}
```

- Deterministic: the same subjects, options and frozen commits give the same
  result. Validators read artifacts and evidence; they do not call other
  systems.
- A result lists findings with codes and severities, the verdict, and any report
  offered (a verification report, a trace report), which become evidence.
- The engine ships one validator provider with the standard set (`docs/04`
  section 10.2). An organisation adds its own as another provider.

### 3.9 Toolset

An external MCP server whose tools an agent role may use, for example Jira,
Confluence or an observability system.

```csharp
interface IToolsetProvider : IProvider {
    Task<..IReadOnlyList<ToolDescriptor>> ListToolsAsync();
    Task<..ToolResult> CallAsync(string tool, JsonDocument arguments, ToolCallContext context);
}
```

- The engine MCP server **proxies** these tools to the agent. The external
  server's credentials stay with the engine, never in the sandbox (from FF's
  design for external MCP servers).
- A role names toolsets and, optionally, a tool allow-list within each. Version 1
  connects no external toolsets: the engine refuses at load a `toolset`
  provider type, a role's `toolsets` and a library item's
  `requires.toolsets`, each naming its key, as it does `policy.toolsets`
  (LOG 273, LOG 339). This section describes the design for when
  toolsets are built (section 5).
- Each call is recorded on the timeline with the step and the tool. A tool that
  writes (creating a ticket) is marked as writing in its descriptor, so a role
  with `write_access: none` cannot call it unless the role allows it explicitly.

### 3.10 Notifications

```csharp
record NoCapabilities;   // notifications declare no flags; every provider threads replies (F16)

interface INotificationsProvider : IProvider {
    Task<..MessageRef> SendAsync(string channel, Notification message, MessageRef? thread);
    Task<..MessageRef> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread);
}
```

- Slack first; no chat-specific types outside its provider.
- Events, by name: `decision_needed` (with its kind), `reminder`, `resumed`,
  `completed`, `failed`, `stalled`, `stuck`. Configuration routes and filters by
  these names. One thread per root run; children reply in it (from FF).
- `reminder` is the decision request again for a wait still open after each
  `notifications.reminder_interval` (`docs/04` section 4), with
  `DecisionRequest.Reminder` its number (0 for the first request); Slack titles
  it "Reminder <n>". Sent once per wait and number.
- `PostDecisionAsync` does not wait for the answer. A chat answer reaches the
  inbound gateway and becomes an API decision call.
- De-duplicated per run, step, attempt and event, so a restart does not repeat a
  message. A failed send never affects the run.

### 3.11 Session surface

```csharp
interface ISessionSurfaceProvider : IProvider {
    Task<..SessionHandle> PrepareAsync(SessionWorkspace workspace);   // how the person reaches it
}
```

- Version 1: hand-off to the person's own agent tool on their machine (RW18),
  kind `local`: instructions for `ff session` and its MCP server (`ff session
  mcp`). With no surface configured the engine gives the same (LOG 243).
- Submission and checks go through the API, not the surface.

### 3.12 Artifact store, record store, secrets

```csharp
interface IArtifactStoreProvider : IProvider {
    Task<..ArtifactRef> PutAsync(Stream content);
    Task<..Stream>      GetAsync(ArtifactRef reference);
    Task<..Uri>         GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl);
}

interface IRecordStoreProvider : IProvider {
    Task<..Unit>           AppendAsync(RecordBatch records);   // runs, steps, decisions, overrides, findings, evidence, cost, timeline
    Task<..QueryResult<T>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, object> parameters);
}

interface ISecretsProvider : IProvider {
    Task<..SecretValue> ResolveAsync(SecretReference reference);
}
```

- The artifact store is immutable and content-addressed. Named pointers (the
  latest revision of a logical artifact) are records, not blobs.
- The record store takes named queries, not SQL, so a provider cannot leak its
  storage engine into the interface. The query catalogue is in the engine
  (`Queries`, `FeatureFlow.Providers.Abstractions/Stores.cs`).
- Every record kind is declared once (`RecordKinds`,
  `FeatureFlow.Providers.Abstractions/RecordKinds.cs`; F17): its `RecordBatch`
  list, table (and `kind` value in a shared table), key and other columns,
  write rule and the change it makes. Every named query but `open_waits`,
  `changes` and `latest_change` is declared over its kind (`RecordQueries`): its
  forms by the parameters given, filters, order and limit. The SQL stores have
  one writer and one query builder that read the declarations, and keep only
  their dialect (JSON extraction, the closing wait's body patch, `COLLATE "C"`,
  Postgres's NUL escape) and the three store-native queries. A new kind is one
  batch list and one declaration, with no store code. Text orders ordinally in
  every store. The declarations name today's tables and columns, so existing
  databases open unchanged. There is no in-memory record store; tests use
  SQLite on a temporary file.

  | Write rule | Kinds |
  |---|---|
  | Replace if not older (`updated_at`) | runs |
  | Replace | locks, agent steps, sessions, session checks, findings, step states, pull requests, repositories |
  | Keep first | timeline, artifacts, notifications, finding events, activity |
  | Append | answers, reports, agent events, overrides |
  | A closing record keeps the opening one's body, set closed | waits |
  | An opening record is kept once; an answer or timeout replaces | questions |

- The record store numbers the changes each batch makes, in a `changes` table
  written inside `AppendAsync`'s own transaction, so no writer changes and none
  is missed (LOG 397): one `run_changed` per run whose records of a kind
  declared to change it (runs, timeline, answers, overrides, artifacts,
  sessions, findings, finding events, step states, pull requests) the batch
  writes, one `wait_opened` or `wait_closed` per wait record, and one
  `step_changed` per run and step whose activity it writes (`ChangeKinds.Of`,
  computed from the declarations; LOG 527).
  `Queries.Changes` returns those after a sequence (`after`, ascending, at most
  500) and `Queries.LatestChange` the latest; `GET /events` reads them
  (`docs/02` section 7). The numbers are in commit order, so a reader that keeps
  its place misses nothing: SQLite has one writer; Postgres takes a
  transaction-scoped advisory lock before writing the changes, held to the
  commit, since identity order alone is not commit order. Rows are not pruned.
- `open_waits` answers across runs: every open wait of a `starting` or
  `running` run, with its run, oldest first, at most 500, for `GET /attention`
  (`docs/02` section 7). It joins waits to runs on the run's status, so a run
  that ended without closing its waits (a terminated workflow, set by
  `  reconcile`) shows none. SQLite and Postgres index the open waits by
  organisation (`waits_open`, a partial index on `open`; Postgres schema script
  2). A wait that
  opened and closed between two records is never written (LOG 374), so it never
  appears. Known limit: the 500 are the organisation's oldest open waits, taken
  before `GET /attention` filters them by caller, so with more than 500 open a
  caller's newer waits drop out of its list; paging by the caller's scope is
  not in version 1.
- For the console (LOG 527): `step_states` answers a run's step counters
  (executions, attempts used, allowed with grants, granted), the last written
  per step; `pull_requests` a run's pull request records, the last written per
  repository and number; `repositories` the registered repositories by name;
  `recent_answers` an actor's answers, newest first, to a limit. Each is its own
  table in SQLite and Postgres (schema script 4), body as JSON, upserted. A wait record carries
  an intervention's `cause` and a patch approval's `patch`; a run record its
  level, parent step, tracker issue with title and link, and start (LOG 528).
  Older records lack them, and nothing is back-filled.
- Secret values are scrubbed at the logging layer.

### 3.13 Credential broker

```csharp
record CredentialScope(IReadOnlyList<string> Repositories, IReadOnlyDictionary<string, string> Permissions);

interface ICredentialBroker : IProvider {
    Task<..Credential> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl);
    Task<..Credential> RefreshAsync(Credential credential);
}
```

| Actor | Gets |
|---|---|
| The engine | Push and merge on the code host; GitHub Issues writes; CI triggers. Never permission to change branch protection |
| An agent step | No code host credential (LOG 95): its checkout arrives as a Git bundle with no remote. An agent that must read another repository will need a read-only credential, scoped to it and the run's repositories, for the step's lifetime (F5); not in version 1. No tracker identity |
| A model proxy | The model key. Never the agent (F6). Each agent runtime instance has its own, from its `model_access`, and a step's token selects its instance's (LOG 446). It forwards, by its `auth`, only `POST v1/messages` and `v1/messages/count_tokens` (`api_key`, `oauth`), or `GET meta`, `GET models`, `POST v1/messages`, `POST v1/messages/count_tokens` and `POST chat/completions` with the key as `Authorization: Bearer` (`github`, the account's Copilot API, whose models must offer one of those; `POST responses` is not forwarded; LOG 496), to its upstream's own scheme, host and port, and refuses anything else before reading the key (LOG 104). A `github` key is a fine-grained personal access token with only the Copilot Requests permission |

The broker refuses a request wider than it may grant; it never silently narrows
one. Spike 2 issued a 60-minute, one-repository token and refused wider
requests (`archive/docs/16` section 2).

---

## 4. Packaging

| Rule | Detail |
|---|---|
| Version 1 | Providers are in-process .NET packages. A host project references the providers it ships; configuration selects them by name |
| Organisation customisation | A **distribution repository**, not a fork: the organisation's own providers, a small host project that builds its worker and API images, and its configuration. A fork is needed only for core changes, and is acceptable for bespoke authentication handlers (`docs/00` P13). Version 1 has no published packages or sample host, so today a distribution needs the engine's source; both are planned (`docs/12` R1, review F6) |
| Later | A remote provider adapter for providers in other languages or needing isolation |

---

## 5. Not in version 1

| Item | Why |
|---|---|
| Windows sandbox provider | `docs/02` section 4.4 |
| Deployment provider | Delivery in version 1 is merging and reporting (RW15) |
| Feature-flag provider | Workflow concern; add when a workflow needs it |
| Session surfaces other than hand-off | Review rounds on a pull request, console chat, chat-thread questions: later |
| VCS systems other than Git | The interface allows one |
| Remote provider adapter | Section 4 |
| Toolset providers (section 3.9) | Product owner, 2026-11-21: not in version 1. Their settings are refused at load rather than accepted and ignored (LOG 339) |

---

## 6. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entries 35 to 37.
