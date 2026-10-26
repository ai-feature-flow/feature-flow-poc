# Provider interfaces, first pass

Status: shape only, revised for review 05. Date: 2026-10-26. Deliverable 2 of
`brief.md` section 11. Companion to `docs/01-architecture-first-pass.md` section 4.

**Scope of this pass.** Every interface is given its capability record and its
method signatures. Per-method contracts — exact inputs and outputs, idempotency
keys, error codes, timeouts, heartbeat behaviour — are **not** written yet. They
follow once the shape is agreed.

**Review 05 applied.** The main change is a new rule: **no provider method waits
on human or external time** (section 1.3). That removed `AskAsync`,
`WaitForSubmissionAsync`, `RunCheckAsync` and `WaitForRunAsync`, and added an
`InboundEvents` capability. Also applied: code host gaps (P2), agent runtime
result and resume (P3), sandbox checkout, file transfer and progress (P4), prior
findings as a review input (P5), tracker search and field mapping (P6), and
moving named pointers from the artifact store to the record store (P7).

Language is C#, per DP2. The interfaces are transport-neutral by rule (section
4.5 of the architecture document), so a remote provider adapter stays possible
later even though it is not built in version 1.

---

## 1. Rules every interface follows

| Rule | Detail |
|---|---|
| Base contract | Every provider implements `IProvider`: `Describe()` and `CheckAsync()` |
| Async | Every method is `Task`-returning |
| Cancellation | Every method takes a `CancellationToken` |
| **Short and bounded** | **No provider method waits on human or external time.** Waiting is an engine mechanism: durable timers, polling, or inbound events. See section 1.3 |
| Classified result | Every method returns `ProviderResult<T>` for expected outcomes. Exceptions only for programming errors and bugs; the engine's decorator turns an unexpected exception into an infrastructure error with a distinct reason so it is visible |
| Transport-neutral | Signatures use plain serialisable records only. No third-party SDK types (no Octokit, no Temporal types, no cloud SDK types) |
| Capabilities | Declared as typed records, never strings. A step checks before use and fails with a named missing capability |
| Credentials | No provider reads credentials from configuration. All go through `ICredentialBroker` (section 4.6) |
| Observability | The engine decorates every call, so providers get spans, timeouts and error classification without extra code. Providers only add attributes (section 4.8) |
| Contract tests | One suite per interface, shipped with the engine (S5) |

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

`ProviderResult<T>` is a struct so the transport-neutral rule holds: it
serialises without a third-party dependency.

### 1.2 Capability negotiation

Runs at run start (S3), so a run fails in the first minute rather than at step 40.
The engine asks each configured provider for its capabilities, compares them with
what the plan's steps require, and fails with a named missing capability.

Negotiation runs **again** if `feature-flow.yaml` changes mid-run and removes a
capability the run needs (section 5.1.2).

`Requires` declares a capability a provider needs **from another provider** — for
example "code host with `ReviewComments`". Negotiation checks it. Where the
dependency is real rather than declarative, prefer composition: the provider
receives the other through dependency injection (see the AI review case, section 7).

### 1.3 No provider method waits on human or external time

Added for review 05 (P1). A human may answer days later, and a CI run may take an
hour. Neither can block inside a provider call, because decisions reach the
workflow as Temporal Updates through the API (Q4) and because a blocked activity
is indistinguishable from a stuck one.

- Provider methods are **short, bounded calls**.
- Waiting is done by the engine: durable timers, polling, or inbound events.
- A provider that can receive push events declares an **`InboundEvents`**
  capability and translates incoming webhooks into engine events through the API.
- Anything a human does arrives as an API action and becomes an Update.

### 1.4 Two groups of providers

Added for review 05 (answer 5). The credential broker is swappable but not
step-facing, so the catalogue splits in two. **The same rules apply to both.**

| Group | Members | Used by |
|---|---|---|
| Step providers | Sandbox, agent runtime, code host, CI, tracker, review, notifications, session surface | Engine steps |
| Platform services | Credential broker, secrets, artifact store, record store | The engine itself |

---

## 2. Sandbox provider

Creates the environment for one agent step. Receives the **resolved manifest**
and runs the tiers for its **execution target** (section 5.1.1). It never parses
`feature-flow.yaml` or `devcontainer.json`.

```csharp
public sealed record SandboxCapabilities(
    IReadOnlyList<string> Os,          // "linux", "windows"
    bool Docker,
    bool NetworkPolicy,
    bool PersistentAcrossSteps,        // can the sandbox outlive a worker crash (3.6)
    bool StreamingOutput);             // can ExecuteAsync stream output for heartbeats

public interface ISandboxProvider : IProvider {
    Task<ProviderResult<SandboxHandle>> CreateAsync(
        ResolvedManifest manifest,
        ExecutionTarget target,
        CheckoutSpec checkout,          // repository, branch, credential from the broker
        IReadOnlyList<MountSpec> mounts, // input artifacts to place in the sandbox
        string idempotencyKey,
        CancellationToken ct);

    Task<ProviderResult<SandboxHandle>> AttachAsync(string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CommandResult>> ExecuteAsync(
        SandboxHandle handle, TierCommand command, IProgress<CommandProgress>? progress, CancellationToken ct);

    Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle handle, ArtifactRef source, string path, CancellationToken ct);

    Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle handle, string path, CancellationToken ct);

    Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle handle, CancellationToken ct);
}
```

Notes:

- `AttachAsync` is the re-attachment path for resumable agent steps (section 3.6).
  A provider without `PersistentAcrossSteps` returns a classified error and the
  step degrades to creating a new sandbox.
- `ExecuteAsync` covers both agent steps and test tiers. The distinction is the
  command and its limits, not the method (review 05, answer 3).
- **Long commands need progress.** `IProgress<CommandProgress>` feeds heartbeats;
  the prototype lost steps to idle timeouts. A provider without `StreamingOutput`
  must still report periodic progress, or the step cannot heartbeat.
- File transfer is either mounts declared at creation or `CopyIn`/`CopyOut`.
- Windows is a capability, not a special case. Version 1 ships no Windows
  provider; the hooks are in place (section 4.7).

---

## 3. Agent runtime provider

Under pattern C (section 1.4) the runtime runs **inside** the sandbox. This
provider starts and supervises it; it does not relay tool calls.

```csharp
public sealed record AgentRuntimeCapabilities(
    bool StructuredOutput,
    bool Streaming,
    bool ToolAllowlist,
    int MaxContextTokens,
    bool Hooks,            // can the runtime call back to the engine per tool call
    bool SessionResume);

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

- `GetProgressAsync` returns session size or turn count. That is what the attempt
  budget progress rule needs: a resume is free only if the session has grown
  (section 3.7).
- **`GetResultAsync` is the source of truth for the outcome**: outcome, structured
  output, produced artifact references, session reference, usage. The session-end
  hook is a signal, not the source of truth (review 05, P3).
- `ResumeAsync` takes the sandbox handle as well as the session reference, because
  the session lives inside a sandbox that may have been re-attached or recreated.
- A runtime without `Hooks` gets process-level control only, and the engine
  records the coarser control in the run.
- Model credentials are short-lived and scoped to the step, issued by the
  credential broker, preferably through a model gateway.

### 3.1 The hook endpoint

Not a provider interface: an engine HTTP contract that the agent runtime calls
from inside the sandbox. Shape only.

| Hook | Payload | Engine response |
|---|---|---|
| Session start | run id, step id, per-step token | Ack, policy for the step |
| Before tool call | tool name, arguments | Allow, or deny with a reason |
| After tool call | tool name, result summary, token usage | Ack |
| Session end | outcome, session reference | Ack |

The engine uses these for per-tool-call policy checks, structured events, token
and cost accounting, and heartbeats. The per-step token is issued by the
credential broker and scoped to this run and step's hook endpoint (section 5.4).

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
    bool InboundEvents);                  // can receive webhooks (section 1.3)

public interface ICodeHostProvider : IProvider {
    // Branches
    Task<ProviderResult<BranchRef>> CreateBranchAsync(RepoRef repo, string name, string fromRef, string idempotencyKey, CancellationToken ct);

    // Pull requests
    Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pr, PullRequestSpec spec, CancellationToken ct);
    Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pr, string idempotencyKey, CancellationToken ct);

    // Merging
    Task<ProviderResult<MergeResult>> MergeBaseForwardAsync(RepoRef repo, string branch, string into, CancellationToken ct);
    Task<ProviderResult<MergeResult>> MergeAsync(PullRequestRef pr, string method, CancellationToken ct);
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

- `EnqueueMergeAsync` is the `MergeQueue` path. When the target branch requires a
  queue, a direct merge is refused, so the merge step enqueues and waits. The
  engine's merge lock remains the default for integration branches (section 6.3).
- Every object the engine creates carries a hidden idempotency marker (section
  7.1), which is why `idempotencyKey` appears on the create methods and why
  `FindByMarkerAsync` exists: on retry a step searches for the marker and adopts
  the existing object instead of creating a duplicate (S4).
- `GetPullRequestAsync` returns state, head SHA, approvals and mergeability. This
  is what "the human approves on the code host and the engine waits" needs (Q5).
- `GetCheckStatusAsync` takes `RepoRef` as well as the SHA: a SHA alone is not
  unique across repositories.
- `GetDiffAsync` returns an artifact reference, not the diff itself, so a large
  aggregate diff never enters workflow state.
- `CreateRevertPullRequestAsync` serves the revert option of a partial delivery
  (section 7.3). `ClosePullRequestAsync` serves an abort that explicitly asks for
  it.

**`RequestAiReviewAsync` is removed.** An AI review on a PR (for example Copilot
review) is a **review provider** implementation that uses the code host provider
to request the review and read the comments. It is composed, not a code host
method (review 05, P2).

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

- CI delegation is the default for Docker-dependent tiers (Q2), so this provider
  is on the main path, not a fallback.
- It must report results for **queue builds** (for example the GitHub Actions
  `merge_group` event), because the merge queue path depends on it.
- **`WaitForRunAsync` is removed** (review 05, P1). Waiting is an engine
  mechanism: a durable timer plus polling through `GetRunStatusAsync`, or an
  inbound event. `GetRunStatusAsync` is non-blocking and must handle checks that
  register late.
- `InboundEvents` declares that the provider can receive push events and translate
  them into engine events through the API (section 1.3).

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

- Agents have **no** tracker identity. Tracker writes go through engine steps only
  (section 4.6).
- `SearchIssuesAsync` finds existing issues by marker (idempotency, S4) and lists
  an epic's children.
- **Field mapping is configuration, not code.** Organisations use custom fields
  (story points, components, team fields). `IssueSpec` carries engine-level
  fields; the provider maps them to provider-specific fields through its own
  configuration. `CustomFields` declares support.

---

## 7. Review provider

```csharp
public sealed record ReviewCapabilities(
    IReadOnlyList<string> Scopes,   // "ticket", "epic", "delivery"
    bool StructuredFindings,
    bool Fingerprints);

public interface IReviewProvider : IProvider {
    Task<ProviderResult<ReviewResult>> ReviewAsync(
        ReviewScope scope,
        IReadOnlyList<ArtifactRef> inputs,
        IReadOnlyList<string> checks,
        IReadOnlyList<FindingSummary> priorFindings,   // from the ledger, same scope
        CancellationToken ct);
}
```

Notes:

- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet. **Never on line numbers** (section 7.2). A provider without
  `Fingerprints` cannot guarantee a declined finding stays declined, so the engine
  records that.
- Epic scope reviews the aggregate diff (DP10).
- **`priorFindings` is required** (review 05, P5). The prototype's spec check was
  blind to its earlier findings and its verdicts changed between passes. Passing
  the prior findings for the same scope is what makes a verdict stable across
  passes (R4). Fix verification needs the original finding as an input for the
  same reason.
- The same provider is used for the **on-demand** check action in interactive
  sessions (section 7.4), so a check never appears for the first time after
  submission.
- An AI review on a PR is a review provider implementation composed with the code
  host provider, not a code host method (section 4).

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

- Immutable and content-addressed (Q2). A new version is a new hash plus a named
  pointer; **the pointer lives in the record store**, not here (review 05, P7).
  `PutPointerAsync` and `ResolveAsync` were removed from this interface for that
  reason; see section 9.
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

- Relational, queried through the API (Q3). The timeline is append-only. This is
  the long-term audit record (Q13).
- **Named pointers live here**, not in the artifact store (review 05, P7). A new
  artifact version is a new hash plus a named pointer, and the pointer is a record.
- `QueryAsync` is named-query based rather than raw SQL, so the schema can evolve
  and so a provider cannot leak its storage engine into the interface. The query
  catalogue lives in the engine, in one place (review 05, answer 2).
- The workflow reads the delivery ledger through an activity, never directly
  (section 7.3).
- `org_id` is part of the key from version 1 (Q15).

---

## 10. Secrets provider

```csharp
public interface ISecretsProvider : IProvider {
    Task<ProviderResult<SecretValue>> ResolveAsync(SecretReference reference, CancellationToken ct);
}
```

Secrets are referenced from configuration, never stored in it. Values are scrubbed
at the logging layer (section 4.8).

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
- **`AskAsync` is replaced by `PostDecisionRequestAsync`** (review 05, P1). A human
  may answer days later, so the answer cannot block a provider call. The method
  posts the request and returns a message reference. The chat platform's callback
  goes to an inbound endpoint, which the provider translates into an **API decision
  call** — the same path as any other decision (Q4).
- `Interactive` declares that a human can answer in-channel. The policy model
  states which decisions may be made from chat (section 5.4).
- Covers "run stuck" as well as "decision needed" (S13).

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
  credential. GitHub App tokens, service accounts, personal tokens and future
  mechanisms (OIDC workload identity) are implementations.
- **No provider reads credentials directly from configuration.** This is what
  keeps identity choices from leaking into provider interfaces (section 4.6).
- Agents in the sandbox get a token minted per step: one repository, minimum
  permissions, about an hour, refreshable through the hook endpoint.

---

## 13. Session surface provider

Added for Q20. The interactive session itself is an engine mechanism; **where the
human works** is the pluggable part.

```csharp
public interface ISessionSurfaceProvider : IProvider {
    Task<ProviderResult<SessionHandle>> PrepareAsync(SessionWorkspace workspace, CancellationToken ct);
}
```

Notes:

- Version 1 ships surface A only: hand-off to the human's own agent tool on their
  own machine. Surfaces B, C and D are later and optional (section 7.4).
- **The surface only prepares the workspace and tells the human how to reach it**
  (review 05, P1). Two methods were removed:
  - `WaitForSubmissionAsync` — submission comes from the human's CLI or MCP through
    the API and becomes an Update, like every other decision.
  - `RunCheckAsync` — checks are run by the engine (validators, review providers),
    not by the surface. In surface A the human's CLI or MCP calls the API, and the
    engine runs the check.
- Hooks and policy do not apply in the human's environment. The trust boundary is
  the submission, and the human is the accountable actor in the audit record.

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
| Remote provider adapter | Interfaces are transport-neutral so it stays possible; not built (section 4.5) |
| Windows sandbox provider | Hooks are in v1, the provider is not (section 4.7) |
| Feature-flag provider | Not an engine concern in v1 (section 1.5) |
| Session surfaces B, C, D | Only surface A ships (section 7.4) |

---

## 16. Open questions

All five questions from the first pass were answered in review 05 and are applied
above:

| # | Question | Answer |
|---|---|---|
| 1 | `ProviderResult<T>` vs exceptions | Keep `ProviderResult<T>` for expected outcomes. Exceptions only for programming errors and bugs; the decorator turns an unexpected exception into an infrastructure error with a distinct reason (section 1) |
| 2 | Named queries vs query object | Named queries, with the catalogue in the engine in one place (section 9) |
| 3 | Split `ExecuteAsync` | Keep one method. Agent runtimes and test tiers both run commands; the difference is the command and its limits (section 2) |
| 4 | Provider-to-provider capability dependencies | `Requires` on `ProviderDescriptor`, checked at run-start negotiation. Prefer composition through dependency injection where the dependency is real (sections 1.2, 4, 7) |
| 5 | Is the credential broker a provider? | Swappable but not step-facing. The catalogue splits into **step providers** and **platform services**; same rules for both (section 1.4) |

### Still open

1. **Per-method contracts.** Not written yet: exact inputs and outputs,
   idempotency keys, error codes per method, timeouts, heartbeat behaviour. This
   is the next step for this document.
2. **The `InboundEvents` shape.** Section 1.3 says a provider translates incoming
   webhooks into engine events through the API, but the endpoint contract is not
   defined. It is the same kind of contract as the agent hook endpoint (section
   3.1) and probably shares its shape.
3. **Whether `IProgress<CommandProgress>` is the right mechanism** for sandbox
   execution progress. It is idiomatic .NET but is not serialisable, which sits
   awkwardly with the transport-neutral rule if a remote provider adapter is ever
   built.
