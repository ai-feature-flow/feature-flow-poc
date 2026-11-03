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
| Base interface | `Describe()` returns name, version, typed capability records, capabilities required from other providers, and a configuration schema. `CheckAsync()` is called at startup and run start |
| Capabilities | Typed records, never strings. A caller checks before use and fails with a named missing capability. A run checks every capability its plan needs when it starts (P9) |
| Short and bounded | No method waits on human or external time. Long work is started and polled; waiting is the engine's (`docs/02` section 2.3) |
| Classified results | Expected failures are `ProviderError` values with a class and a reason (section 1.1). Exceptions are programming errors; the engine's decorator turns one into an infrastructure error with reason `internal_error`, so it is visible |
| Every success mapped | Every documented success response of the remote system maps to a result, including empty ones such as GitHub's 204 for a merge with nothing to merge. An unmapped success is `unexpected_response`, a **work** error, so it is never retried forever (Spike 3, `archive/docs/18` section 4) |
| Idempotent creates | A create method takes an idempotency key and writes a hidden marker into the object. On `timeout` the caller finds by marker and adopts before retrying, because the object may already exist |
| Credentials | Only from the credential broker (section 3.13). No provider reads a credential from configuration |
| Transport-neutral | Plain serialisable records only; no third-party SDK types. A remote provider adapter (gRPC or HTTP, for providers in other languages) stays possible; it is not built in version 1 |
| Observability | The engine's decorator adds spans, timeouts and classification; providers add attributes only |
| Contract tests | One suite per interface, shipped with the engine. It includes every documented success response. A provider conforms only if it passes |

### 1.1 Error classification

| Reason | Class | Retry | Meaning |
|---|---|---|---|
| `unreachable`, `server_error`, `internal_error` | Infrastructure | Yes, with backoff | The remote or the network failed; the work did not happen |
| `timeout` | Infrastructure | Yes | The work **may** have happened: creates find by marker first |
| `rate_limited` | Infrastructure | Yes, honouring `retry_after` | — |
| `unauthenticated`, `forbidden` | Infrastructure | A short budget, then the run stops with an intervention naming the configuration fault. The budget is 5 tries, waiting 1, 2, 4 and 8 s, within one activity attempt; at run start a spent budget refuses the start (LOG 144) | Almost always configuration |
| `not_found`, `conflict`, `validation_failed`, `rejected` | Work | No | The remote answered; the step maps it to an outcome or an intervention. A permission the provider's identity lacks (GitHub's 403 "Resource not accessible by integration") is `rejected`, named a configuration fault: it does not heal, so it stops for a person at once (LOG 119) |
| `base_moved` | Work | No | A push was refused because the target moved since it was read |
| `capability_missing` | Work | No | — |
| `unexpected_response` | Work | No | An unmapped success |
| `partial` | Work | The step decides | Some of the work happened |

Infrastructure retries never use a step's attempts (`docs/01` section 4). A
step may reclassify a provider's error because it knows the intent (`docs/04`
section 1).

### 1.2 Default timeouts

| Call | Default | Heartbeat |
|---|---|---|
| Metadata read | 15 s | No |
| Write | 30 s | No |
| Sandbox create or dispose | 5 min | Yes |
| Command in a sandbox | Set by the step | Yes |
| Artifact put or get | 5 min | Yes, for large payloads |

---

## 2. Changes from the first design

| Change | Why |
|---|---|
| The review provider is replaced by a **findings source** provider: an external reviewer that returns findings (an AI review on a pull request, SonarCloud, a SARIF file). The standard reviewer is an agent step (RW5) | One way to run an agent |
| New **validator** provider | The plug point for deterministic cross-artifact checks (RW7) |
| New **toolset** provider | External MCP servers for agents, proxied by the engine (from FF) |
| CI provider gains `GetExpectedRunsAsync` | `ci.checks` must tell "no CI" from "not started" without GitHub-specific logic in the step (`docs/04` section 13) |
| Code host loses `CreateBranchAsync` | Work branches are created by a VCS push (`docs/04` section 7) |
| Tracker statuses are mapped from the library's status names in provider configuration | Templates name statuses abstractly (`docs/03` section 2.5) |

---

## 3. The providers

| # | Provider | Used by |
|---|---|---|
| 3.1 | Sandbox | Agent steps, `test.run`, `test.integration`, `analysis.run` |
| 3.2 | Agent runtime | Agent steps |
| 3.3 | VCS | The engine's pushes, catch-up and merges |
| 3.4 | Code host | Pull requests, checks, protection, merge queue, comments |
| 3.5 | CI | Tiers delegated to CI, `ci.checks` |
| 3.6 | Tracker | `tracker.update`, declarative tracker updates, planners' toolsets |
| 3.7 | Findings source | `findings.record` |
| 3.8 | Validator | `artifact.validate` |
| 3.9 | Toolset | Agent steps, through the engine MCP server |
| 3.10 | Notifications | Decisions, `notify.send`, stuck runs |
| 3.11 | Session surface | Interactive sessions |
| 3.12 | Artifact store, record store, secrets | The engine itself |
| 3.13 | Credential broker | Every provider |

Temporal and the worker platform are configuration only (`docs/07`).

LOG 81: while later milestones build the engine actions, a
temporary type `action_stand_in` runs them, scripted or recording, and a
`fake` provider of any type only describes itself. Neither is configured in a
production installation: an installation that configures one must set
`development: true`.

The VCS, code host and CI interfaces as built in M5 differ from the signatures below: catch-up and merge take an
expected revision and a key, the code host reads a file and has no merge method, and CI results are returned as
unparsed files. LOG 107 lists them.
How the Git, GitHub and GitHub Actions providers meet them, and the engine's new `actions: write`, are LOG 108
to 115.

### 3.1 Sandbox

```csharp
record SandboxCapabilities(IReadOnlyList<string> Os, bool Docker, bool NetworkPolicy,
                           bool PersistentAcrossSteps, bool StreamingOutput);

record SandboxSpec(string Key, string Home, IReadOnlyDictionary<string, string> Environment,
                   IReadOnlyList<string> Folders);

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
- Will receive the **resolved manifest** (the repository's `feature-flow.yaml`
  merged with any referenced `devcontainer.json` and defaults), not yet built. It
  never parses either file itself.
- `PersistentAcrossSteps`: the sandbox outlives a worker restart. Without it, a
  worker loss is treated as a lost sandbox.
- Read-only checkouts are enforced by the sandbox (`MakeReadOnlyAsync`), not the
  agent.
- Commands are started and polled; `AttachCommandAsync` re-attaches to a running
  command after a worker restart (Spike 1, `archive/docs/15` section 3.4).
- Every sandbox has the engine's hook client and MCP endpoint configured, with
  the per-step token (`docs/02` section 4.2).
- Network policy, CPU and memory limits are applied where the capability is
  declared. FF's containers had none (`../feature-factory/issues.md`).

### 3.2 Agent runtime

```csharp
record AgentRuntimeCapabilities(bool Hooks, bool Mcp, bool SessionResume, bool StreamingToolCalls,
                                bool UsageReporting, bool ModelConfiguration, bool CustomEndpoint,
                                int? MaxContextTokens);

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
  (`docs/04` section 3.3); `GetResultAsync` gives how the session ended, its
  usage, and the file report of a runtime without engine tools. A session-end
  hook only wakes the engine.
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

### 3.3 VCS

```csharp
record VcsCapabilities(string System, bool HiddenReferences, bool Squash, bool PartialClone);

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
    // The remote, through the engine's own clones (docs/02 section 6)
    Task<..string?>      ResolveAsync(RepoRef repo, string reference);   // null when absent
    Task<..bool>         DescendsFromAsync(RepoRef repo, string revision, string ancestor);
    Task<..PushResult>   PushAsync(RepoRef repo, string reference, string revision,
                                   CommitBundle? commits, string? expectedOldRevision);
    // M5
    Task<..CatchUpResult> CatchUpAsync(RepoRef repo, string parent, string work);
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
record CodeHostCapabilities(bool PullRequest, bool ReviewComments, bool CheckStatus, bool BranchProtection,
                            bool MergeQueue, bool InboundEvents);

interface ICodeHostProvider : IProvider {
    Task<..PullRequestRef>   OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key);
    Task<..PullRequestRef>   UpdatePullRequestAsync(PullRequestRef pr, PullRequestSpec spec);
    Task<..PullRequestState> GetPullRequestAsync(PullRequestRef pr);   // state, head, approvals, merged by whom
    Task<..Unit>             ClosePullRequestAsync(PullRequestRef pr, string? comment);
    Task<..PullRequestRef>   CreateRevertPullRequestAsync(PullRequestRef pr, string key);
    Task<..MergeResult>      EnqueueMergeAsync(PullRequestRef pr);
    Task<..BranchProtection> GetBranchProtectionAsync(RepoRef repo, string branch);
    Task<..string>           GetDefaultBranchAsync(RepoRef repo);
    Task<..CheckStatus>      GetCheckStatusAsync(RepoRef repo, string commit);
    Task<..IReadOnlyList<ReviewThread>> ListReviewThreadsAsync(PullRequestRef pr);
    Task<..CommentRef>       AddCommentAsync(PullRequestRef pr, ReviewComment comment);
    Task<..Unit>             ResolveThreadAsync(ReviewThread thread);
    Task<..FoundObjects>     FindByMarkerAsync(RepoRef repo, string marker);
}
```

- The default branch is always read from here, never from configuration or the
  plan (F1, F10).
- `GetBranchProtectionAsync` only checks protection exists; the engine's
  identity has no permission to change it (F5).
- `GetPullRequestAsync` reports who merged, so `scm.merge` can detect a person's
  merge on the code host.

### 3.5 CI

```csharp
record CiCapabilities(bool Trigger, bool ReadLogs, bool Artifacts, bool InboundEvents, bool QueueBuilds);

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
- `HasCiAsync` answers only "is CI configured for this head" (for GitHub
  Actions, a workflow file exists), so `ci.checks` knows to wait a while for a
  first check. Which runs must pass comes from the target branch's required
  checks, not from evaluating workflow triggers (LOG 147).
- `QueueBuilds`: reports results for merge queue builds.

**The CI workflow contract (GitHub Actions; LOG 112, 119).** The provider dispatches a tier's job (its workflow file, from
the manifest's `ci.job`) on the repository's **default branch**, so the workflow definition and what it does with the
repository's secrets are reviewed code, never a file the agent edited. A workflow used as a CI job must:

- be triggered by `workflow_dispatch` and declare an input `commit_sha`; the provider sets it to the revision under
  test and refuses a workflow without it;
- check that revision out (`actions/checkout` with `ref: ${{ inputs.commit_sha }}`) and build and test only it,
  because the run's own `head_sha` is the default branch's and its results are filed under `commit_sha`;
- upload its result files (`.trx`, JUnit `.xml`, `.sarif`) as artifacts, each under 50 MB and the artifact under
  100 MB;
- set `permissions: contents: read`, so the code under test cannot write to the repository through `GITHUB_TOKEN`;
- if it is also triggered by `push` on the branches agents write to (`ff/**`), use no secrets. A push runs the
  workflow file as the pushed commit has it, which the agent may have edited, so any secret that run can reach is
  exposed to the agent's code. Keep secrets for jobs that run only from the default branch (LOG 119).

### 3.6 Tracker

```csharp
record TrackerCapabilities(bool Issues, bool Hierarchy, bool Transitions, bool Links, bool Comments,
                           bool CustomFields, bool Labels);

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
- The provider also backs the tracker tools offered to planners through a
  toolset (section 3.9), with the same identity and a run label on everything
  created.

### 3.7 Findings source

```csharp
record FindingsSourceCapabilities(bool Fingerprints, bool Severity, bool Threads);

interface IFindingsSourceProvider : IProvider {
    Task<..CollectHandle>  RequestAsync(FindingsTarget target, string key);   // e.g. ask for a review of a head
    Task<..CollectStatus>  GetStatusAsync(CollectHandle handle);              // pending, ready, unavailable
    Task<..ArtifactRef>    ReadFindingsAsync(CollectHandle handle);           // a findings report artifact
}
```

- Examples: Copilot review on a pull request (composed with the code host
  provider), SonarCloud, a SARIF file from CI.
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
- A role names toolsets and, optionally, a tool allow-list within each. The
  organisation lists which toolsets exist (`docs/05` section 3.1).
- Each call is recorded on the timeline with the step and the tool. A tool that
  writes (creating a ticket) is marked as writing in its descriptor, so a role
  with `write_access: none` cannot call it unless the role allows it explicitly.

### 3.10 Notifications

```csharp
record NotificationCapabilities(IReadOnlyList<string> Channels, bool Interactive, bool Threads);

interface INotificationsProvider : IProvider {
    Task<..MessageRef> SendAsync(string channel, Notification message, MessageRef? thread);
    Task<..MessageRef> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread);
}
```

- Slack first; no chat-specific types outside its provider.
- Events, by name: `decision_needed` (with its kind), `resumed`, `completed`,
  `failed`, `stalled`, `stuck`. Configuration routes and filters by these names.
  One thread per root run; children reply in it (from FF).
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

- Version 1: hand-off to the person's own agent tool on their machine (RW18).
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
  storage engine into the interface. The query catalogue is in the engine.
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
| The engine | Push and merge on the code host; tracker writes; CI triggers. Never permission to change branch protection |
| An agent step | No code host credential (LOG 95): its checkout arrives as a Git bundle with no remote. An agent that must read another repository will need a read-only credential, scoped to it and the run's repositories, for the step's lifetime (F5), in a later milestone. No tracker identity |
| A model proxy | The model key. Never the agent (F6). It forwards only `POST v1/messages` and `v1/messages/count_tokens` to its upstream's own scheme, host and port, and refuses anything else before reading the key (LOG 104) |

The broker refuses a request wider than it may grant; it never silently narrows
one. Spike 2 issued a 60-minute, one-repository token and refused wider
requests (`archive/docs/16` section 2).

---

## 4. Packaging

| Rule | Detail |
|---|---|
| Version 1 | Providers are in-process .NET packages. A host project references the providers it ships; configuration selects them by name |
| Organisation customisation | A **distribution repository**, not a fork: the organisation's own providers, a small host project that builds its worker and API images, and its configuration. A fork is needed only for core changes |
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

---

## 6. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entries 35 to 37.
