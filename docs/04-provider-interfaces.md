# Provider interfaces

Status: revised 2026-10-31 for the spike results (`docs/15`, `docs/16`,
`docs/18`), the code review design (`docs/19`), the merge design (`docs/20`) and
the product owner's merge model of the same day (squash and engine-merge
parameters, a project level, F1 as a configurable setting).

This document gives each provider its **capability record** and **method
signatures**. The contract for each method (idempotency, timeouts, heartbeats,
error codes, semantics) is in `docs/05-provider-contracts.md`. The architecture
behind the providers is in `docs/01-architecture-first-pass.md` section 4
(providers).

The language is C# (.NET was chosen over Python: `docs/02` DP2). Interfaces are
**transport-neutral** (`docs/01` section 4.5: plain serialisable types, no
third-party SDK types), so a remote provider adapter stays possible later.
Version 1 does not build one.

Terms used throughout:

| Term | Meaning |
|---|---|
| Step | One node of a workflow plan, run by the engine. Step contracts are in `docs/06` |
| Activity | A Temporal unit of work the engine runs for a step. Temporal retries it on failure |
| Sandbox | The isolated environment (container or VM) where an agent and its tools run |
| Agent worker | The engine process that starts and supervises agents. It is **separate from the sandbox**, so a worker restart does not stop the agent |
| Integration branch | An epic's branch (the epic branch). Ticket branches merge into it |
| Project branch | A project's branch. A project run starts epic runs, and epic branches merge into it; it merges into `main` |
| Target branch | The branch a ticket, epic or project merges into |
| Idempotency marker | A hidden marker the engine writes into every remote object it creates, so a retry finds and adopts the object instead of duplicating it (`docs/01` section 7.1) |

---

## 1. Rules every interface follows

| Rule | Detail |
|---|---|
| Base contract | Every provider implements `IProvider`: `Describe()` and `CheckAsync()` |
| Async | Every method returns a `Task` |
| Cancellation | Every method takes a `CancellationToken` |
| **Short and bounded** | **No provider method waits on human or external time.** Waiting is an engine mechanism: durable timers, polling or inbound events (section 1.3) |
| Classified result | Every method returns `ProviderResult<T>` for expected outcomes. Exceptions are only for programming errors; the engine's decorator turns an unexpected exception into an infrastructure error with its own reason, so it is visible |
| **Every success is mapped** | A provider maps every documented success response of its remote system to an outcome, including empty ones (for example GitHub's 204 for a merge with nothing to merge). An unmapped success is a **work** error, so it cannot be retried forever (DR8) |
| Transport-neutral | Signatures use plain serialisable records only. No third-party SDK types (no Octokit, Temporal or cloud SDK types) |
| Capabilities | Declared as typed records, never strings. A step checks before use and fails with a named missing capability |
| Credentials | No provider reads credentials from configuration; all go through the credential broker (section 12). **Agents get read-only code host credentials and never one that can push** (DR13), and **model credentials never enter the agent's environment** (DR1) |
| Observability | The engine wraps every call in a decorator, so providers get spans, timeouts and error classification without extra code. Providers only add attributes (`docs/01` section 4.8, the observability contract) |
| Contract tests | One test suite per interface, shipped with the engine, so any provider can prove it conforms (`docs/02` S5) |

### 1.1 Shared types

```csharp
public interface IProvider {
    ProviderDescriptor Describe();
    Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct);
}

public sealed record ProviderDescriptor(
    string Name,
    string Version,
    IReadOnlyList<object> Capabilities,   // typed capability records
    IReadOnlyList<string> Requires,       // capabilities needed from other providers
    string ConfigSchemaRef);

public enum ErrorClass { Infrastructure, Work }

public sealed record ProviderError(
    ErrorClass Class,
    string Reason,          // stable code: "rate_limited", "not_found", "conflict", ...
    TimeSpan? RetryAfter,
    string? RemoteId);

public readonly record struct ProviderResult<T> {
    public T? Value { get; init; }
    public ProviderError? Error { get; init; }
    public bool IsOk => Error is null;
}
```

| Error class | Engine behaviour |
|---|---|
| Infrastructure | Retried with backoff. Does not use up one of the step's attempts |
| Work | Not retried. The plan routes by outcome |

Which reason codes belong to which class, and what each one means, is the table
in `docs/05` section 1.3. `ProviderResult<T>` is a
struct with no third-party dependency, so it serialises under the
transport-neutral rule.

### 1.2 Capability negotiation

Runs at run start (`docs/02` S3), so a run fails in its first minute rather than
at step 40. The engine asks each configured provider for its capabilities,
compares them with what the plan's steps need, and fails with a named missing
capability.

Negotiation runs **again** if the repository's manifest (`feature-flow.yaml`, read
per step: `docs/01` section 5.1.2) changes mid-run and removes a capability the
run needs.

`Requires` declares a capability a provider needs **from another provider**, for
example "a code host with `ReviewComments`". Negotiation checks it. Where the
dependency is real rather than declared, the provider receives the other provider
through dependency injection (composition). The review providers in section 7 are
the main case.

### 1.3 No provider method waits on human or external time

A human may answer days later and a CI run may take an hour. Neither can block a
provider call: a blocked activity looks the same as a stuck one, and human
decisions reach the workflow as Temporal **Updates** (messages the workflow
validates and answers) through the API (`docs/02` Q4).

- Provider methods are **short, bounded calls**.
- Waiting is done by the engine: durable timers, polling or inbound events.
- **Polling happens inside one long-running activity** that loops and
  heartbeats, not one activity per poll (`docs/05` section 1.2.1). Measured in
  Spike 1: a 60-second step polled every 500 ms cost one activity and 11 history
  events in total, and a 20-second connection break inside it cost nothing extra
  (`docs/15` section 6.2).
- A provider that can receive push events (webhooks) declares an
  **`InboundEvents`** capability. The inbound gateway (section 1.3.2) turns those
  events into engine events.
- Anything a human does arrives as an API action and becomes an Update.

### 1.3.1 Events wake, reads confirm

The rule for anything asynchronous:

> **Events wake, reads confirm.** Hooks, webhooks and chat callbacks only wake
> the workflow. The workflow then reads the truth through a short provider call:
> `GetResultAsync`, `GetRunStatusAsync`, `GetPullRequestAsync`. Human decisions
> stay Updates through the API.

- An event is never a source of truth, only a hint that something may have
  changed.
- Polling is the fallback when an event is lost, not an error path. Spike 2
  dropped a gate-approval event on purpose; polling read the remote state and
  resolved the gate in 2 polls (`docs/16` section 2, check K5: approve a gate from chat, then drop the event).
- This is why `GetResultAsync` (section 3) does not block: the session-end hook
  wakes the workflow, and `GetResultAsync` confirms the outcome.

### 1.3.2 Inbound gateway

One inbound gateway in the API, not one endpoint per provider. For each
provider it:

1. **Verifies the webhook signature.**
2. **Deduplicates** by the remote delivery id.
3. **Translates** the payload into an engine event.

Engine events reach the workflow as Temporal **Signals** (one-way messages) that
only wake it. The workflow then reads state through the provider (section
1.3.1). Human decisions stay Updates through the API.

### 1.4 Two groups of providers

The credential broker can be swapped but is not used by steps, so the catalogue
splits in two. **The same rules apply to both.**

| Group | Members | Used by |
|---|---|---|
| Step providers | Sandbox, agent runtime, code host, CI, tracker, review, notifications, session surface | Engine steps |
| Platform services | Credential broker, secrets, artifact store, record store | The engine itself |

---

## 2. Sandbox provider

Creates the environment for one agent step. It receives the **resolved
manifest** (the repository's `feature-flow.yaml` after configuration layers are
applied) and runs the test tiers for its **execution target** (`docs/01`
section 5.1.1). It never parses `feature-flow.yaml` or `devcontainer.json`
itself.

The agent runs **in the sandbox**, which is separate from the agent worker that
supervises it. A worker restart, including every deploy, costs the agent
nothing: the new worker re-attaches to the sandbox and its running commands.

```csharp
public sealed record SandboxCapabilities(
    IReadOnlyList<string> Os,          // "linux", "windows"
    bool Docker,
    bool NetworkPolicy,
    bool PersistentAcrossSteps,        // the sandbox outlives a worker crash
    bool StreamingOutput);             // GetCommandStatusAsync returns output as it is produced

public interface ISandboxProvider : IProvider {
    Task<ProviderResult<SandboxHandle>> CreateAsync(
        ResolvedManifest manifest,
        ExecutionTarget target,
        CheckoutSpec checkout,          // repository, branch, read-only or writable
        IReadOnlyList<MountSpec> mounts, // input artifacts to place in the sandbox
        string idempotencyKey,
        CancellationToken ct);

    Task<ProviderResult<SandboxHandle>> AttachAsync(string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CommandHandle>> StartCommandAsync(
        SandboxHandle handle, TierCommand command, string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle handle, string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(
        CommandHandle command, string? outputCursor, CancellationToken ct);

    Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken ct);

    Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle handle, ArtifactRef source, string path, CancellationToken ct);

    Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle handle, string path, CancellationToken ct);

    Task<ProviderResult<CommitBundle>> FetchCommitsAsync(SandboxHandle handle, string branch, CancellationToken ct);

    Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle handle, CancellationToken ct);
}
```

Notes:

- **Resume after a crash** (DR9; `docs/01` section 3.6, resumable agent steps).
  `AttachAsync` re-attaches to the sandbox by idempotency key. If the sandbox
  survived, the agent's **uncommitted files are kept**. If it was lost, or the
  provider lacks `PersistentAcrossSteps` (in which case `AttachAsync` returns a
  classified error), the step creates a new sandbox and **restarts from the last
  pushed commit**. Version 1 takes no snapshots of uncommitted work.
- **Commands are started and polled, not awaited.** A command may run for
  hours, which would break the "short and bounded" rule (section 1.3), and an
  awaited command would be lost with a crashed worker even when the sandbox
  survives. `StartCommandAsync` returns a handle; `GetCommandStatusAsync`
  returns status, exit code and output since a cursor; `AttachCommandAsync`
  re-attaches to a **running** command by key after a worker crash. Spike 1 broke
  the worker-to-sandbox connection mid-command: the command kept running and the
  step re-attached by handle without restarting (`docs/15` section 3.4).
- `GetCommandStatusAsync` serves agent steps and test tiers alike. The
  difference is the command and its limits, not the method.
- **Read-only checkouts.** A role that must not change the branch, such as the
  code reviewer (section 7.1), gets a read-only checkout. The sandbox enforces it,
  not the agent (`docs/08` section 2.5, control without hooks).
- **Agents do not push** (DR13). The agent commits inside the sandbox. As part
  of the agent step, the engine takes the commits out with `FetchCommitsAsync`
  and pushes them with its own credential through the code host's `PushAsync`
  (section 4), at the end of the step and at work-in-progress points (section
  2.1).
- `StreamingOutput` means incremental output through the cursor. A provider
  without it returns output only when the command finishes.
- Files reach the sandbox as mounts declared at creation, or through `CopyInAsync`
  and `CopyOutAsync`.
- Windows is a capability (`Os`), not a special case. Version 1 ships no Windows
  provider; the hooks for one are in place (`docs/01` section 4.7).

### 2.1 How an agent's commits reach the code host

The push is part of the agent step (`docs/06` section 2.1, `agent.run`). The
agent never pushes (DR13).

1. `FetchCommitsAsync` fetches the commits on the branch from the sandbox's
   checkout and returns them as a `CommitBundle` (an artifact reference plus the
   head SHA), so no commit content enters workflow state.
2. The code host's `PushAsync` (section 4) pushes them with the engine's own
   credential, at the end of the step and at work-in-progress points.

| Case | What is pushed |
|---|---|
| The step succeeds | The ticket's own branch. This is a push, not a merge |
| The step fails | A hidden work-in-progress ref, not the ticket branch, so a retry can pick the commits up |
| The role has no write access | Nothing. Commits made anyway fail the step (the after-step check of what changed) |
| The step's `push` parameter is false (scratch steps; default true) | Nothing |

---

## 3. Agent runtime provider

The agent runtime (for example Claude Code or GitHub Copilot CLI) runs **inside
the sandbox** with its own tools, and calls back to the engine through hooks
(`docs/01` section 1.4, agent in sandbox with engine hooks). This provider starts
and supervises it. It does not relay tool calls.

```csharp
public sealed record AgentRuntimeCapabilities(
    bool StructuredOutput,
    bool Streaming,
    bool ToolAllowlist,
    int? MaxContextTokens,   // null when the runtime does not expose it
    bool Hooks,              // the runtime calls the engine before and after each tool call
    bool SessionResume,
    bool UsageReporting,     // reports tokens and model per session
    bool ModelConfiguration, // the model can be chosen per step
    bool CustomEndpoint);    // can be pointed at a model endpoint other than the vendor's own

public interface IAgentRuntimeProvider : IProvider {
    Task<ProviderResult<AgentSession>> StartAsync(
        SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken ct);

    Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken ct);

    Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken ct);

    Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken ct);

    Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken ct);
}
```

Notes:

- `GetProgressAsync` returns session size or turn count. The attempt budget
  needs it: a resume is free only if the agent made progress (a new pushed commit
  or a grown session), and after 3 resumes in a row without progress the next
  one counts as an attempt (`docs/01` section 3.7, attempt budgets).
- **`GetResultAsync` is the source of truth for the outcome**: outcome,
  structured output, produced artifact references, session reference and usage.
  The session-end hook only wakes the workflow (section 1.3.1).
- **`ResumeAsync` reloads the agent's conversation from the stored session
  reference** (DR9). The reference is stored by the engine (`docs/01` section
  3.6). Resume must work in a fresh worker process: nothing may come from the
  crashed worker's memory. Spike 3 found Claude Code resume depended on state
  held in the worker process and failed after a restart (`docs/18` section 4,
  item 4). It takes the sandbox handle as well, because the sandbox may have
  been re-attached or recreated (section 2).
- **No push credential** (DR13). The agent commits in the sandbox; the engine
  pushes (section 2.1).
- **Files opened.** `GetResultAsync` includes the files the agent opened,
  recorded from hooks or from the runtime's streamed tool-call output, or
  "unknown" if neither is available. The review coverage output is built from it
  (section 7.1).
- **Model credentials never enter the agent's environment** (DR1). Version 1
  uses either a proxy that adds the key to outbound model requests, or per-run
  keys with a spend limit and an expiry. Each form's remaining risk is recorded
  in `docs/01` section 4.6 (credential broker).
- A runtime without `Hooks` gets process-level control only, and the engine
  records the coarser control in the run. The fallback controls are in
  `docs/08` section 2.5: sandbox-enforced write access, a post-step check of what
  changed, and cost from the runtime's own usage output.
- The code reviewer is an agent run through this provider, on a read-only
  checkout (section 7.1). It can run on any runtime.
- Measured facts for Claude Code and Copilot CLI are in section 3.2.

### 3.1 The hook endpoint

Not a provider interface: an engine HTTP contract that the agent runtime calls
from inside the sandbox. Shape only; the contract is in `docs/05` section 3.1.

| Hook | Payload | Engine response |
|---|---|---|
| Session start | run id, step id, per-step token | Ack, and the policy for the step |
| Before tool call | tool name, arguments | Allow, or deny with a reason |
| After tool call | tool name, result summary, token usage | Ack |
| Session end | outcome, session reference | Ack |

The engine uses the hooks for:

- **policy checks on each tool call** (deny before the tool runs);
- **structured events** for the run's timeline;
- **token and cost accounting** per step (`docs/01` section 5.5);
- **heartbeats**, so a stalled agent can be told from a slow one;
- **the record of files the agent opened**, one of its two sources (the other
  is the runtime's streamed tool-call output; section 3).

The per-step token is issued by the credential broker and is valid only for this
run and step's hook endpoint (`docs/01` section 5.4, API authorisation).

### 3.2 Measured runtime facts

Measured in Spike 2 and the Claude Code proof that followed it (`docs/16`
sections 4 and 12).

| Capability | Claude Code | Copilot CLI |
|---|---|---|
| `Hooks` | **Yes.** Session start, before and after each tool call, and stop all fired, on Windows and macOS. A narrow command allowlist denied a call before it ran | **Partial.** Session start and stop fired. Before- and after-tool-call hooks registered but **did not fire** with a third-party model. Untested with a first-party model |
| `CustomEndpoint` | **No.** Speaks only the Anthropic API and rejects non-Anthropic model names, so it cannot use an OpenAI-compatible model router. Works with a first-party Anthropic subscription or key | **Yes.** Runs through an OpenAI-compatible router (`COPILOT_PROVIDER_BASE_URL`) |
| `MaxContextTokens` | 200,000 | Not exposed |
| `StructuredOutput`, `Streaming`, `ToolAllowlist`, `SessionResume`, `UsageReporting`, `ModelConfiguration` | Yes | Yes |

Consequences:

- **Per-tool-call policy needs the before-tool-call hook.** On Copilot CLI with a
  third-party model it is unavailable, so the fallback controls in `docs/08`
  section 2.5 apply and the run records the coarser control.
- Both runtimes can stream their tool calls, so the record of files an agent
  opened (section 3) does not depend on hooks.
- Whether Claude Code works through a key-adding proxy (the proxy form of DR1)
  was not measured.

---

## 4. Code host provider

```csharp
public sealed record CodeHostCapabilities(
    bool PullRequest,
    bool ReviewComments,
    bool CheckStatus,
    IReadOnlyList<string> MergeMethods,   // "merge", "squash", "rebase"
    bool BranchProtection,
    bool MergeQueue,                      // optional; only some repositories enable it
    bool InboundEvents);                  // can receive webhooks (section 1.3.2)

public interface ICodeHostProvider : IProvider {
    // Branches
    Task<ProviderResult<BranchRef>> CreateBranchAsync(RepoRef repo, string name, string fromRef, string idempotencyKey, CancellationToken ct);

    // Pull requests
    Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pr, PullRequestSpec spec, CancellationToken ct);
    Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pr, string? comment, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pr, string idempotencyKey, CancellationToken ct);

    // Pushing (the engine pushes an agent's commits; section 2.1)
    Task<ProviderResult<PushResult>> PushAsync(RepoRef repo, string refName, CommitBundle commits, string? expectedOldSha, CancellationToken ct);

    // Merging
    Task<ProviderResult<MergeResult>> MergeBaseForwardAsync(RepoRef repo, string branch, string into, CancellationToken ct);
    Task<ProviderResult<MergeResult>> MergeAsync(PullRequestRef pr, bool squash, string testedHeadSha, string expectedBaseSha, CancellationToken ct);
    Task<ProviderResult<MergeResult>> EnqueueMergeAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken ct);

    // Checks
    Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string commitSha, CancellationToken ct);

    // Review comments
    Task<ProviderResult<IReadOnlyList<ReviewThread>>> ListReviewCommentsAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<CommentRef>> AddReviewCommentAsync(PullRequestRef pr, ReviewComment comment, CancellationToken ct);
    Task<ProviderResult<CommentRef>> ReplyToReviewCommentAsync(CommentRef comment, string body, CancellationToken ct);
    Task<ProviderResult<Unit>> ResolveReviewThreadAsync(ReviewThread thread, CancellationToken ct);

    // Diffs
    Task<ProviderResult<ArtifactRef>> GetDiffAsync(RepoRef repo, string fromRef, string toRef, CancellationToken ct);

    // Idempotency
    Task<ProviderResult<FoundObjects>> FindByMarkerAsync(RepoRef repo, string marker, CancellationToken ct);
}
```

Notes:

- How a merge works, without a merge lock, is in section 4.1.
- **Agents never push** (DR13). The engine pushes their commits with
  `PushAsync` (section 2.1) and performs every merge it is allowed to. As a
  backstop, code host rules allow only the engine's identity to update epic and
  project branches.
- **Merging into `main`** is governed by setting F1 (the engine may not merge
  into the default branch). It is configurable, and its default is the safe
  one, so by default a human merges into `main` (section 4.1). The engine
  enforces it at plan validation and again at merge time, reading the default
  branch from the code host (DR2). Code host protection on `main` (requiring
  independent review) is the final fallback; it is set by an identity the engine
  does not hold, and the engine never holds a bypass. `GetBranchProtectionAsync`
  only checks that it exists (DR3).
- **Merge queue** (`EnqueueMergeAsync`, capability `MergeQueue`) is optional.
  Where a repository requires a queue, a direct merge is refused, so the merge
  step enqueues and then waits by polling or inbound event. It is not the
  default; it is the managed answer if merge contention ever becomes real
  (`docs/20`).
- **Every documented success response is mapped** (DR8). GitHub answers a
  catch-up with nothing to merge with 204 and no body; that is a completed merge
  with the branch unchanged, not an error. Spike 3 found the unmapped 204 being
  retried forever as an infrastructure error (`docs/18` section 4, item 2).
- Every object the engine creates carries an idempotency marker, which is why
  the create methods take `idempotencyKey` and why `FindByMarkerAsync` exists: on
  retry a step finds the marker and adopts the existing object instead of
  creating a duplicate (`docs/02` S4).
- `GetPullRequestAsync` returns state, head SHA, approvals and mergeability.
  Where the code host requires its own review, the human approves there and the
  engine reads the approval through this method (`docs/02` Q5).
- `GetCheckStatusAsync` takes the repository as well as the SHA: a SHA alone is
  not unique across repositories.
- `GetDiffAsync` returns an artifact reference, not the diff, so a large diff
  never enters workflow state.
- `CreateRevertPullRequestAsync` serves the revert option when a delivery across
  several repositories only partly succeeds (`docs/01` section 7.3).
  `ClosePullRequestAsync` serves an abort that explicitly asks for it, and
  closes a squash-merged pull request with a comment (section 4.1).
- **No `RequestAiReviewAsync`.** An AI review on a pull request (for example
  Copilot review) is a **review provider** that uses this provider to request the
  review and read the comments (section 7).

### 4.1 How merging works

From the merge design (`docs/20`) and the product owner's merge model. There is
**no merge lock**; work merges the way developers do.

1. **Catch up.** `MergeBaseForwardAsync` merges the target branch into the
   branch being delivered. `branch` is the target branch (merged **from**);
   `into` is the ticket or epic branch (merged **into**). The result reports
   whether `into` changed; tests re-run only if it did.
2. **Merge.** The merge step has two parameters (`docs/06` section 6.5, the
   merge step):
   - `squash`: a squash merge if true, a plain merge if false;
   - `engine_can_merge`: if true the engine merges; if false the engine raises
     a human decision request, waits, a human merges on the code host, and the
     engine detects the merge through `GetPullRequestAsync` and continues.

| Merge | `squash` | `engine_can_merge` |
|---|---|---|
| Ticket branch into epic branch | true | true |
| Epic branch into project branch | false (each ticket's commit stays visible) | true |
| Project branch into `main` | false | false by default (setting F1) |

**When the engine merges**, `MergeAsync` builds the merge or squash commit from
exactly the tested head (`testedHeadSha`) and pushes it to the target. If the
target has moved since catch-up (it is no longer `expectedBaseSha`), Git refuses
the push. The outcome is `base_moved`, and the step routes back to catch-up. The
merge step's attempt allowance (default 3, counting every attempt) bounds the
retries, then escalates; like every limit it is configurable.

After a plain merge, the code host marks the pull request merged because its
commits reach the target. After a **squash** merge it does not, so the engine
closes the pull request with a comment linking the squash commit.

**Setting F1** (the engine may not merge into the default branch) replaces the
old fixed floor with the same default. It is set by organisation, then
repository, then workflow, and the organisation can lock it. Nothing an agent can
edit can change it: the repository setting is read from the repository's
configuration on its default branch, never the ticket branch, and a re-plan
during a run cannot change it.

---

## 5. CI provider

```csharp
public sealed record CiCapabilities(bool Trigger, bool ReadLogs, bool Artifacts, bool InboundEvents);

public interface ICiProvider : IProvider {
    Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, string tier, string commitSha, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken ct);
    Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken ct);
    Task<ProviderResult<LogRef>> ReadLogsAsync(CiRunRef run, CancellationToken ct);
    Task<ProviderResult<IReadOnlyList<ArtifactRef>>> GetArtifactsAsync(CiRunRef run, CancellationToken ct);
}
```

Notes:

- Test tiers that need Docker run in CI by default (`docs/03` Q2), so this
  provider is on the main path, not a fallback. Spike 2 dispatched such a tier to
  GitHub Actions; the call returned in 22.7 s without waiting for the run, and
  polling then read the result (`docs/16` section 2, check R5: a Docker tier run through CI).
- There is no wait method. The engine waits with a durable timer plus
  `GetRunStatusAsync`, or an inbound event (section 1.3). `GetRunStatusAsync` does
  not block and must handle checks that register late.
- Where a repository uses a merge queue, the provider must report results for
  **queue builds** (for example the GitHub Actions `merge_group` event).
- `InboundEvents` declares that the provider can receive push events (section
  1.3.2).

---

## 6. Tracker provider

```csharp
public sealed record TrackerCapabilities(bool Issues, bool Epics, bool Transitions, bool Links, bool Comments, bool CustomFields);

public interface ITrackerProvider : IProvider {
    Task<ProviderResult<IssueRef>> GetIssueAsync(string issueKey, CancellationToken ct);
    Task<ProviderResult<IReadOnlyList<IssueRef>>> SearchIssuesAsync(IssueQuery query, CancellationToken ct);
    Task<ProviderResult<IssueRef>> CreateIssueAsync(IssueSpec spec, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<IssueRef>> UpdateIssueAsync(IssueRef issue, IssueSpec spec, CancellationToken ct);
    Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string toState, CancellationToken ct);
    Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken ct);
    Task<ProviderResult<Unit>> CommentAsync(IssueRef issue, string body, CancellationToken ct);
}
```

Notes:

- Agents have **no** tracker identity. Tracker writes go through engine steps
  only (`docs/01` section 4.6).
- `SearchIssuesAsync` finds existing issues by idempotency marker and lists an
  epic's children.
- **Field mapping is configuration, not code.** Organisations use custom fields
  (story points, components, team). `IssueSpec` carries engine-level fields; the
  provider maps them to its own fields through its configuration. `CustomFields`
  declares support.

---

## 7. Review provider

```csharp
public sealed record ReviewCapabilities(
    IReadOnlyList<string> Scopes,   // "ticket", "epic", "project", "delivery"
    bool StructuredFindings,
    bool Fingerprints);

// A code review sets Repo, Branch and Base. A review of a specification
// document sets Document instead.
public sealed record ReviewTarget(
    RepoRef? Repo, string? Branch, string? Base, ArtifactRef? Document);

public interface IReviewProvider : IProvider {
    Task<ProviderResult<ReviewResult>> ReviewAsync(
        ReviewScope scope,
        ReviewTarget target,
        IReadOnlyList<ArtifactRef> specRefs,           // what to review against
        IReadOnlyList<ArtifactRef> machineResults,     // build, tests, linters, coverage
        IReadOnlyList<string> checks,
        IReadOnlyList<FindingSummary> priorFindings,   // from the findings ledger, same scope
        CancellationToken ct);
}
```

Notes:

- The version 1 reviewer is an agent on a read-only checkout (section 7.1).
- The arguments mirror the `review.run` step's inputs (`docs/06` section 8.2):
  `branch` and `base` (in `target`), `spec_refs` (the specification documents to
  review against) and `machine_results`. A review of a specification document
  rather than code passes the document in `target`. `checks` names what to
  review for.
- **`priorFindings` is required.** The findings ledger (every finding and its
  status) is the source of truth; a review pass is evidence (`docs/17` section
  6.5, positions L1 to L4). Passing prior findings for the same scope makes
  verdicts more stable across passes; it does not make them stable, which is why
  the ledger decides (DR4). At epic scope, prior findings include the child
  tickets' ledgers (DR11). Fix verification passes the original finding for the
  same reason.
- Findings are **fingerprinted** on rule, repository, path, symbol and
  normalised snippet, **never on line numbers** (`docs/01` section 7.2). A
  provider without `Fingerprints` cannot guarantee a declined finding stays
  declined, and the engine records that.
- The same provider serves the **on-demand** check in interactive sessions
  (`docs/01` section 7.4), so a check never appears for the first time after
  submission. A developer's pre-submit run matches the gated one when it uses
  the same brief, tools, model and commit.
- An **AI review on a pull request** (for example Copilot review) is another
  review provider, composed with the code host provider (section 4).

### 7.1 The agent reviewer

From the code review design (`docs/19`). The version 1 reviewer is **an agent on
a read-only checkout of the branch**: a review provider composed with the sandbox
provider (section 2) and the agent runtime provider (section 3).

| Aspect | Design |
|---|---|
| What it is told | The branch, the base to compare against, and the changed files. It reads the diff itself, opens any other file, and may build and run tests. Nothing else is pre-loaded |
| What it reviews against | A specification: the ticket's acceptance criteria, the epic's contract, the repository's conventions |
| What runs first | Machine checks (build, tests, linters, analysers, coverage of changed lines). The reviewer gets their results |
| Angles | Separate passes for correctness, design and structure, tests, security, and conformance to the specification |
| Evidence | A bug finding carries a throwaway failing test where possible. Demonstrated findings block; reasoned ones are labelled and ranked lower |
| Model | A different, ideally stronger, model than the author is **workflow policy**, not enforced by the engine (another model may not be available). The engine records which model authored and which reviewed |
| Coverage | **An engine mechanism, never an engine rule.** The engine publishes a coverage output: changed files, files read, files not read, and whether the record is verified (section 3). It never fails a step for coverage. A review step may set `require_coverage` (`none` by default, `all`, or a list of paths), or a route condition may act on the output; the library's standard code-review template sets `all` |
| Runtime | Any. Files read come from hooks or streamed tool-call output |
| Levels | **Ticket:** the ticket branch against the integration branch, given the ticket and the epic contract. **Epic:** the integration branch against `main` |

---

## 8. Artifact store provider

```csharp
public sealed record ArtifactStoreCapabilities(bool ContentAddressed, bool SignedUrls, bool RetentionPolicy);

public interface IArtifactStoreProvider : IProvider {
    Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken ct);
    Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken ct);
    Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken ct);
}
```

Notes:

- Immutable and content-addressed (`docs/02` Q2). A new version is a new hash
  plus a named pointer, and **the pointer lives in the record store** (section
  9), not here.
- Holds specs, plans and every plan revision, diffs, transcripts, logs and
  reports. Workflow state holds references only.

---

## 9. Record store provider

```csharp
public interface IRecordStoreProvider : IProvider {
    Task<ProviderResult<Unit>> UpsertRunAsync(RunRecord run, CancellationToken ct);
    Task<ProviderResult<Unit>> UpsertStepRunAsync(StepRunRecord stepRun, CancellationToken ct);
    Task<ProviderResult<Unit>> AppendTimelineAsync(TimelineEvent evt, CancellationToken ct);
    Task<ProviderResult<Unit>> RecordFindingAsync(FindingRecord finding, CancellationToken ct);
    Task<ProviderResult<Unit>> RecordTriageAsync(TriageRecord triage, CancellationToken ct);
    Task<ProviderResult<Unit>> RecordDecisionAsync(DecisionRecord decision, CancellationToken ct);
    Task<ProviderResult<Unit>> RecordSteeringAsync(SteeringRecord steering, CancellationToken ct);
    Task<ProviderResult<Unit>> RecordCostAsync(CostRecord cost, CancellationToken ct);
    Task<ProviderResult<Unit>> UpsertDeliveryLedgerAsync(DeliveryLedgerEntry entry, CancellationToken ct);
    Task<ProviderResult<Unit>> PutPointerAsync(string name, ArtifactRef reference, CancellationToken ct);
    Task<ProviderResult<ArtifactRef>> ResolvePointerAsync(string name, CancellationToken ct);
    Task<ProviderResult<QueryResult<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, object> parameters, CancellationToken ct);
}
```

Notes:

- Relational, queried through the API (`docs/02` Q3). The timeline is
  append-only. This is the long-term audit record (`docs/02` Q13).
- **Named pointers live here**, not in the artifact store. A new artifact
  version is a new hash plus a named pointer, and the pointer is a record.
- `QueryAsync` takes a named query, not raw SQL, so the schema can change and a
  provider cannot leak its storage engine into the interface. The query
  catalogue lives in the engine, in one place.
- The workflow reads the delivery ledger (which repositories delivered, for a
  delivery across several) through an activity, never directly (`docs/01`
  section 7.3).
- `org_id` is part of the key from version 1 (`docs/02` Q15: one organisation
  per installation, keyed for later).

---

## 10. Secrets provider

```csharp
public interface ISecretsProvider : IProvider {
    Task<ProviderResult<SecretValue>> ResolveAsync(SecretReference reference, CancellationToken ct);
}
```

Configuration refers to secrets; it never contains them. Values are scrubbed at
the logging layer (`docs/01` section 4.8).

---

## 11. Notifications provider

```csharp
public sealed record NotificationCapabilities(IReadOnlyList<string> Channels, bool Interactive, bool InboundEvents);

public interface INotificationsProvider : IProvider {
    Task<ProviderResult<Unit>> SendAsync(string channel, NotificationMessage message, CancellationToken ct);
    Task<ProviderResult<MessageRef>> PostDecisionRequestAsync(string channel, DecisionRequest request, CancellationToken ct);
}
```

Notes:

- Chat is pluggable: Slack first, others later through the same interface. **No
  Slack-specific types outside the Slack provider.**
- `PostDecisionRequestAsync` posts the request and returns a message reference.
  **It does not wait for the answer**: a human may answer days later. The chat
  platform's callback reaches the inbound gateway (section 1.3.2), and the answer
  becomes an **API decision call**, the same path as any other decision
  (`docs/02` Q4).
- `Interactive` declares that a human can answer in the channel. The policy
  model states which decisions may be made from chat (`docs/01` section 5.4).
- Covers "run stuck" as well as "decision needed" (`docs/02` S13).

---

## 12. Credential broker

```csharp
public interface ICredentialBroker : IProvider {
    Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, string scope, TimeSpan ttl, CancellationToken ct);
    Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken ct);
}
```

Notes:

- Given an actor, a target system and a scope, it returns a short-lived
  credential. GitHub App tokens, service accounts, personal tokens and later
  mechanisms (OIDC workload identity) are implementations.
- **No provider reads credentials from configuration.** This keeps identity
  choices out of provider interfaces (`docs/01` section 4.6).
- **Agents get read-only code host credentials, per step** (DR13: never one
  that can push). Scope: the run's repository, plus any other repositories the
  workflow declares the agent may read (for example to verify a contract). The
  engine pushes agents' commits and performs merges with its own identity.
- **Model credentials never enter the agent's environment** (DR1; section 3).
- The broker refuses a request wider than it may grant; it never silently
  narrows it. Spike 2 minted a one-repository token whose 60-minute expiry came
  from the remote, and refused a wider access level, another repository and a
  TTL over the maximum (`docs/16` section 2, check R6: short-lived, scoped credentials).

---

## 13. Session surface provider

An interactive session is one where a human does the work instead of an agent.
The session itself is an engine mechanism; **where the human works** is the
pluggable part.

```csharp
public interface ISessionSurfaceProvider : IProvider {
    Task<ProviderResult<SessionHandle>> PrepareAsync(SessionWorkspace workspace, CancellationToken ct);
}
```

Notes:

- Version 1 ships surface A only: hand-off to the human's own agent tool on
  their own machine. Surfaces B, C and D are later and optional (`docs/01`
  section 7.4).
- **The surface only prepares the workspace and tells the human how to reach
  it.** It has no other methods:
  - Submission comes from the human's CLI or MCP server through the API and
    becomes an Update, like every other decision.
  - Checks are run by the engine (validators, review providers), not by the
    surface. The human's CLI or MCP server asks the API, and the engine runs the
    check.
  - A "merge when ready" tool, if offered, only asks the engine to run the merge
    step. It never merges itself (`docs/20`).
- Hooks and policy do not apply in the human's environment. The trust boundary
  is the submission, and the human is the accountable actor in the audit record.

---

## 14. Configuration-only providers

These have no behavioural interface; they are connection and placement
configuration.

| Provider | Contents |
|---|---|
| Temporal platform | Address, namespace, auth (API key, mTLS), TLS, payload codec |
| Worker platform | Where orchestration and agent workers run: local Docker or Kubernetes |

---

## 15. Not in version 1

| Item | Why |
|---|---|
| Remote provider adapter | Interfaces are transport-neutral so it stays possible; not built (`docs/01` section 4.5) |
| Windows sandbox provider | The hooks for it are in version 1, the provider is not (`docs/01` section 4.7) |
| Feature-flag provider | Not an engine concern in version 1 (`docs/01` section 1.5, engine mechanism versus workflow policy) |
| Session surfaces B, C, D | Only surface A, hand-off to the human's own tool, ships (`docs/01` section 7.4) |

---

## 16. Open questions

These questions are answered and applied above:

| Question | Answer, and where |
|---|---|
| `ProviderResult<T>` or exceptions | `ProviderResult<T>` for expected outcomes; exceptions for bugs only (section 1) |
| Named queries or a query object | Named queries, catalogue in the engine (section 9) |
| One command method or several | Start and poll; `GetCommandStatusAsync` serves agents and test tiers (section 2) |
| Provider-to-provider dependencies | `Requires`, checked at run start; composition where the dependency is real (sections 1.2, 7) |
| Is the credential broker a provider | Yes, as a platform service with the same rules (section 1.4) |
| The `InboundEvents` shape | One inbound gateway (section 1.3.2) |
| Progress callbacks | Removed; output comes through a cursor on `GetCommandStatusAsync` (section 2) |
| How the engine pushes an agent's commits | `FetchCommitsAsync` from the sandbox, then `PushAsync` on the code host, as part of the agent step (section 2.1) |
| Merge methods | `squash` is a merge parameter, beside `engine_can_merge` (section 4.1) |
| The agent reviewer's inputs | Branch and base, specification references and machine results (section 7) |

### Still open

1. **A long agent review** is one `ReviewAsync` call, not started and polled
   like other agent work (section 7.1).
2. **Review at project level.** `project` is a review scope, but which branch a
   project or epic review compares against (`main` or the project branch) is
   not stated (section 7.1).
