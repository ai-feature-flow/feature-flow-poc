# Provider interfaces, first pass

Status: shape only. Date: 2026-10-26. Deliverable 2 of `brief.md` section 11.
Companion to `docs/01-architecture-first-pass.md` section 4.

**Scope of this pass.** Every interface is given its capability record and its
method signatures. Per-method contracts — exact inputs and outputs, idempotency
keys, error codes, timeouts, heartbeat behaviour — are **not** written yet. They
follow once the shape is agreed.

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
| Classified result | Every method returns `ProviderResult<T>`; no method throws for an expected failure |
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
    bool PersistentAcrossSteps);       // can the sandbox outlive a worker crash (3.6)

public interface ISandboxProvider : IProvider {
    Task<ProviderResult<SandboxHandle>> CreateAsync(
        ResolvedManifest manifest, ExecutionTarget target, string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<SandboxHandle>> AttachAsync(string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CommandResult>> ExecuteAsync(
        SandboxHandle handle, TierCommand command, CancellationToken ct);

    Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle handle, CancellationToken ct);
}
```

Notes:

- `AttachAsync` is the re-attachment path for resumable agent steps (section 3.6).
  A provider without `PersistentAcrossSteps` returns a classified error and the
  step degrades to creating a new sandbox.
- `ExecuteAsync` covers both agent steps and test tiers. The distinction is the
  command, not the method.
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

    Task<ProviderResult<AgentSession>> ResumeAsync(string sessionRef, CancellationToken ct);

    Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken ct);

    Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken ct);
}
```

Notes:

- `GetProgressAsync` returns session size or turn count. That is what the attempt
  budget progress rule needs: a resume is free only if the session has grown
  (section 3.7).
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
    bool AiReviewRequest,
    bool BranchProtection,
    bool MergeQueue);                     // optional; only some repositories enable it

public interface ICodeHostProvider : IProvider {
    Task<ProviderResult<BranchRef>> CreateBranchAsync(RepoRef repo, string name, string fromRef, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<MergeResult>> MergeBaseForwardAsync(RepoRef repo, string branch, string into, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pr, PullRequestSpec spec, CancellationToken ct);
    Task<ProviderResult<MergeResult>> MergeAsync(PullRequestRef pr, string method, CancellationToken ct);
    Task<ProviderResult<MergeResult>> EnqueueMergeAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(string commitSha, CancellationToken ct);
    Task<ProviderResult<CommentRef>> AddReviewCommentAsync(PullRequestRef pr, ReviewComment comment, CancellationToken ct);
    Task<ProviderResult<CommentRef>> ReplyToReviewCommentAsync(CommentRef comment, string body, CancellationToken ct);
    Task<ProviderResult<Unit>> RequestAiReviewAsync(PullRequestRef pr, CancellationToken ct);
    Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken ct);
}
```

Notes:

- `EnqueueMergeAsync` is the `MergeQueue` path. When the target branch requires a
  queue, a direct merge is refused, so the merge step enqueues and waits. The
  engine's merge lock remains the default for integration branches (section 6.3).
- Every object the engine creates carries a hidden idempotency marker (section
  7.1), which is why `idempotencyKey` appears on the create methods.

---

## 5. CI provider

```csharp
public sealed record CiCapabilities(bool Trigger, bool WaitForRun, bool ReadLogs, bool Artifacts);

public interface ICiProvider : IProvider {
    Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, string tier, string commitSha, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<CiRunResult>> WaitForRunAsync(CiRunRef run, CancellationToken ct);
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
- `WaitForRunAsync` must handle checks that register late.

---

## 6. Tracker provider

```csharp
public sealed record TrackerCapabilities(bool Issues, bool Epics, bool Transitions, bool Links, bool Comments);

public interface ITrackerProvider : IProvider {
    Task<ProviderResult<IssueRef>> GetIssueAsync(string issueKey, CancellationToken ct);
    Task<ProviderResult<IssueRef>> CreateIssueAsync(IssueSpec spec, string idempotencyKey, CancellationToken ct);
    Task<ProviderResult<IssueRef>> UpdateIssueAsync(IssueRef issue, IssueSpec spec, CancellationToken ct);
    Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string toState, CancellationToken ct);
    Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken ct);
    Task<ProviderResult<Unit>> CommentAsync(IssueRef issue, string body, CancellationToken ct);
}
```

Note: agents have **no** tracker identity. Tracker writes go through engine steps
only (section 4.6).

---

## 7. Review provider

```csharp
public sealed record ReviewCapabilities(
    IReadOnlyList<string> Scopes,   // "ticket", "epic", "delivery"
    bool StructuredFindings,
    bool Fingerprints);

public interface IReviewProvider : IProvider {
    Task<ProviderResult<ReviewResult>> ReviewAsync(
        ReviewScope scope, IReadOnlyList<ArtifactRef> inputs, IReadOnlyList<string> checks, CancellationToken ct);
}
```

Notes:

- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet. **Never on line numbers** (section 7.2). A provider without
  `Fingerprints` cannot guarantee a declined finding stays declined, so the engine
  records that.
- Epic scope reviews the aggregate diff (DP10).
- The same provider is used for the **on-demand** check action in interactive
  sessions (section 7.4), so a check never appears for the first time after
  submission.

---

## 8. Artifact store provider

```csharp
public sealed record ArtifactStoreCapabilities(bool ContentAddressed, bool SignedUrls, bool RetentionPolicy);

public interface IArtifactStoreProvider : IProvider {
    Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken ct);
    Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken ct);
    Task<ProviderResult<Unit>> PutPointerAsync(string name, ArtifactRef reference, CancellationToken ct);
    Task<ProviderResult<ArtifactRef>> ResolveAsync(string name, CancellationToken ct);
    Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken ct);
}
```

Notes:

- Immutable and content-addressed (Q2). A new version is a new hash plus a named
  pointer; the pointer lives in the record store.
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
    Task<ProviderResult<QueryResult<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, object> parameters, CancellationToken ct);
}
```

Notes:

- Relational, queried through the API (Q3). The timeline is append-only. This is
  the long-term audit record (Q13).
- `QueryAsync` is named-query based rather than raw SQL, so the schema can evolve
  and so a provider cannot leak its storage engine into the interface.
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
public sealed record NotificationCapabilities(IReadOnlyList<string> Channels, bool Interactive);

public interface INotificationsProvider : IProvider {
    Task<ProviderResult<Unit>> SendAsync(string channel, NotificationMessage message, CancellationToken ct);
    Task<ProviderResult<DecisionAnswer>> AskAsync(string channel, DecisionRequest request, CancellationToken ct);
}
```

Notes:

- Chat is pluggable: Slack first, others later through the same interface. **No
  Slack-specific types outside the Slack provider.**
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
    Task<ProviderResult<SessionSubmission>> WaitForSubmissionAsync(SessionHandle handle, CancellationToken ct);
    Task<ProviderResult<ReviewResult>> RunCheckAsync(SessionHandle handle, string checkId, CancellationToken ct);
}
```

Notes:

- Version 1 ships surface A only: hand-off to the human's own agent tool on their
  own machine. Surfaces B, C and D are later and optional (section 7.4).
- `RunCheckAsync` is the pre-submit check: any validator or review check exposed
  as an on-demand action, returning the same structured findings as the gated
  step.
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

## 16. Open questions for this review

1. **`ProviderResult<T>` versus exceptions.** I have chosen a result type so no
   method throws for an expected failure. The alternative is exceptions with a
   classification attribute. The result type is more transport-neutral; exceptions
   are more idiomatic C#. Worth settling before the contracts are written.
2. **Named queries versus a query object** for `IRecordStoreProvider.QueryAsync`.
   Named queries keep the schema private but need a catalogue of query names.
3. **Whether `ISandboxProvider.ExecuteAsync` should be split** into agent
   execution and test-tier execution. One method is simpler; two make the
   capability checks clearer.
4. **How a provider declares that it needs a capability from another provider.**
   For example, the CI provider must report queue builds for the code host's
   `MergeQueue` path. Today that is implicit.
5. **Whether `ICredentialBroker` is a provider at all.** It is called by the
   engine and by the hook endpoint, not by steps. It may belong outside the
   provider catalogue.
