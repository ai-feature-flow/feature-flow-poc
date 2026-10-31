# Provider method contracts

Status: revised 2026-10-31 for the spike results (`docs/15`, `docs/16`,
`docs/18`), the code review design (`docs/19`), the merge design (`docs/20`) and
the product owner's decisions of the same day (merge parameters,
workflow-defined branch levels, a VCS provider).

This document gives the **contract for each provider method**: required
capability, idempotency, timeout and heartbeat, error codes, and what the caller
may rely on. The signatures and capability records are in
`docs/04-provider-interfaces.md`. Sections 2 to 13 here match the provider
sections there (section 2 is the sandbox in both, and so on). The VCS provider
is section 16 here and section 17 there.

Terms used throughout:

| Term | Meaning |
|---|---|
| Step | One node of a workflow plan, run by the engine (`docs/06`) |
| Activity | A Temporal unit of work the engine runs for a step. Temporal retries it on failure |
| Heartbeat | A periodic "still alive, here is progress" report from a long activity, so Temporal can tell a slow call from a dead one |
| Idempotency marker | A hidden marker written into every remote object the engine creates, so a retry finds and adopts it (section 1.1) |
| Parent branch | The branch a piece of work was taken from. Work is reviewed against its parent and merged back into it, level by level up to `main`. The levels (for example ticket, epic, project) are workflow-defined |
| Hidden work-in-progress reference | A Git reference that is not a branch (for example `refs/ff/wip/<ticket>`), where pushes go while an agent step runs (section 16.1) |

---

## 1. How to read a contract

| Element | Meaning |
|---|---|
| Requires | The capability the provider must declare (`docs/04` gives each provider's capability record). The caller checks before use and fails with a named missing capability |
| Idempotency | How a retry avoids duplicating work. `key` means the caller passes a deterministic key (section 1.1); `none` means the call is naturally safe to repeat |
| Timeout | The default. A step may tighten it, never loosen it beyond the policy ceiling |
| Heartbeat | Whether the call must report progress while it runs |
| Errors | Reason codes beyond the common set in section 1.3. Anything not listed falls back to section 1.3 |
| Semantics | What the caller may rely on, where the signature alone is ambiguous |

### 1.1 Idempotency keys

A key is `hash(runId, stepId, stableInputs)`. **Stable inputs exclude anything
that changes per attempt**: no timestamps, attempt numbers or session
references. A resumed attempt must produce the same key as the attempt it
continues. That is what lets a retry re-attach to an existing sandbox
(`docs/01` section 3.6, resumable agent steps) or adopt an existing branch or
pull request (`docs/02` S4).

Two mechanisms (`docs/01` section 7.1, idempotency):

1. **Marker-based discovery.** The provider writes the marker into every remote
   object it creates. On retry the caller searches by marker and adopts the
   object.
2. **Provider-side deduplication**, where the remote system supports a key.

### 1.2 Timeouts

| Class of call | Default | Heartbeat |
|---|---|---|
| Metadata read | 15s | No |
| Write | 30s | No |
| Sandbox create or dispose | 5 min | Yes |
| Command execution | Set by the step, bounded by its `start_to_close` (the activity's overall time limit) | Yes |
| Artifact put or get | 5 min | Yes, for large payloads |
| Review | Set by the step | Yes |

**No method waits on human or external completion** (`docs/04` section 1.3). A
step that must wait uses a durable timer and polls, or waits for an inbound
event.

### 1.2.1 Polling happens inside one activity

> **Poll inside one long-running activity.** The activity loops on
> `GetCommandStatusAsync` or `GetProgressAsync` and heartbeats. History cost is
> **one activity per step**. After a worker crash, the activity's retry
> re-attaches by handle.

One short activity per poll would not scale: a 4-hour agent step polled every
30 seconds would add about 480 activities and over 1,000 history events, which
alone can hit Temporal's history limit (risk R1 in `docs/08`, history
growth). Spike 1 measured the single-activity form: a 60-second step polled
every 500 ms cost 1 activity and 11 history events, and a 20-second connection
break inside it cost no extra activity (`docs/15` section 6.2).

This applies to every poll loop: sandbox commands, agent progress, CI run
status, pull request state, and the fallback when an inbound event is lost. Use
a **durable timer** for waits that may be long and idle (a human gate); use the
**in-activity loop** for waits on work that is actively progressing.

### 1.3 Error codes

Each reason has a default class. **Infrastructure** reasons are retried with
backoff, and the retry does not use up one of the step's attempts. **Work**
reasons are not retried; the plan routes by outcome. What each reason says about
whether the work happened is in its row. A step may override the class, because
it knows the intent; each step's "Error overrides" row in `docs/06` states its
overrides.

| Reason | Class | Retry | Meaning |
|---|---|---|---|
| `unreachable` | Infrastructure | Yes, backoff | Network or DNS failure |
| `timeout` | Infrastructure | Yes | The call exceeded its timeout. **The work may or may not have happened** (section 1.4) |
| `server_error` | Infrastructure | Yes | 5xx from the remote |
| `rate_limited` | Infrastructure | Yes, honour `RetryAfter` | 429 |
| `internal_error` | Infrastructure | Yes | Unexpected exception in the provider. Set by the engine's decorator, with its own reason so it is visible |
| `unauthenticated` | Infrastructure | Short budget, then fail | 401. Almost always a configuration fault (`docs/02` S9) |
| `forbidden` | Infrastructure | Short budget, then fail | 403. Same |
| `not_found` | Work | No | The object does not exist |
| `conflict` | Work | No | Remote state conflicts: merge conflict, branch already exists |
| `base_moved` | Work | No | A push was refused because the target branch or reference moved since it was read. For a merge, the step routes back to catch-up (section 16) |
| `validation_failed` | Work | No | The request was rejected as invalid |
| `rejected` | Work | No | Branch protection or policy refused it |
| `partial` | Work | Step decides | Some of the work happened; usually reconcile, then retry once |
| `capability_missing` | Work | No | The provider does not declare the needed capability |
| `unexpected_response` | Work | No | The remote answered with a success response the provider does not map (section 1.5) |

### 1.4 The timeout ambiguity

A `timeout` on a **create** method is ambiguous: the object may have been
created. Every create method therefore follows this rule:

> On `timeout`, do not retry blindly. Call the matching find method and adopt
> the object if it exists. Only if it does not exist, retry the create with the
> same key.

This is why the code host has `FindByMarkerAsync` and why sandbox `CreateAsync`
is paired with `AttachAsync`.

### 1.5 Every documented success response is mapped

A provider maps **every documented success response** of its remote system to an
outcome, including empty ones (DR8). Any success response it does not map is
returned as **`unexpected_response`, a work error**, never as infrastructure.

Why: an infrastructure error is retried free and indefinitely. Spike 3's code
host read GitHub's 204 (no body) for a catch-up with nothing to merge as a
server error, and retried it forever; the first ticket to merge would have hung
(`docs/18` section 4, item 2). The contract tests for each interface include
every documented success response.

---

## 2. Sandbox provider

The agent runs in the sandbox, separate from the agent worker that supervises
it, so a worker restart (including every deploy) costs nothing: the new worker
re-attaches.

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `CreateAsync` | — | `key` | 5 min | Yes | `conflict` (already exists), `capability_missing` (no provider for the requested `Os`) | Creates the environment and performs the checkout, read-only or writable as the checkout spec says. On `timeout`, call `AttachAsync` with the same key before retrying |
| `AttachAsync` | `PersistentAcrossSteps` | `key` | 30s | No | `not_found` (no sandbox for this key) | Re-attaches to a sandbox that outlived a worker crash. Its **uncommitted files are kept**. Returns `not_found` rather than creating a sandbox; the step then creates one from the hidden work-in-progress reference, with a fresh agent session |
| `StartCommandAsync` | — | `key` | 30s | No | `validation_failed` (unknown command) | Starts a command and returns a handle. **Does not wait for it to finish** |
| `AttachCommandAsync` | `PersistentAcrossSteps` | `key` | 30s | No | `not_found` | Re-attaches to a **running** command by key after a worker crash, so a long command is not lost when the sandbox survives |
| `GetCommandStatusAsync` | — | none | 15s | No | `not_found` | Returns status, exit code, and output since `outputCursor`. Does not block. **The step polls it inside one long-running activity** (section 1.2.1) |
| `CancelCommandAsync` | — | `key` | 30s | No | `not_found` | Cancelling a finished command succeeds |
| `CopyInAsync` | — | none | 5 min | Yes | `not_found` (source artifact) | Places an input artifact into the sandbox |
| `CopyOutAsync` | — | none | 5 min | Yes | `not_found` (path) | Returns an `ArtifactRef`; the content goes to the artifact store, not to workflow state |
| `DisposeAsync` | — | `key` | 5 min | Yes | — | Disposing twice succeeds. A sandbox that outlives a worker crash is **not** disposed because the worker failed |

Notes:

- **Resume after a crash** (DR9). If the sandbox survived, uncommitted files are
  kept and the agent's conversation is reloaded. If it was lost, the step
  restarts from the hidden work-in-progress reference with a **fresh agent
  session**; the agent sees the pushed commits and a note that the previous
  attempt was interrupted. Version 1 takes no snapshots of uncommitted work and
  does not save the conversation mid-step.
- **Commands are started and polled, not awaited.** A command may run for
  hours; awaiting it would break the "short and bounded" rule and lose the
  command with a crashed worker even when the sandbox survives. Spike 1 broke
  the worker-to-sandbox connection mid-command: the command kept running and
  the step re-attached by handle (`docs/15` section 3.4).
- `CreateAsync` gets its checkout credential from the credential broker, never
  from configuration. **The agent holds only a read-only code host credential**
  (DR13; section 12).
- **The push is part of the agent step.** The engine detects, fetches and
  pushes the agent's commits through the VCS provider (section 16.1).
- A read-only checkout (for the code reviewer, and for any role that may not
  write) is enforced by the sandbox, not by the agent.
- `GetCommandStatusAsync` serves agent steps and test tiers alike; the
  difference is the command and its limits.
- `StreamingOutput` means output arrives incrementally through the cursor. A
  provider without it returns output only at completion, which conforms but is
  coarser.

---

## 3. Agent runtime provider

The runtime (for example Claude Code or Copilot CLI) runs inside the sandbox;
this provider starts and supervises it. Measured capabilities of both runtimes
are in `docs/04` section 3.2.

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `StartAsync` | — | `key` | 30s | No | `validation_failed` (task or limits), `capability_missing` | Starts the runtime inside the sandbox and returns a session handle. Does **not** wait for the agent to finish |
| `ResumeAsync` | `SessionResume` | `key` | 30s | No | `not_found` (session gone) | **Reloads the conversation from the stored session reference** (DR9). Must work in a fresh worker process: nothing comes from the crashed worker's memory. Used only when the sandbox survived and was re-attached; after a lost sandbox the step calls `StartAsync` for a fresh session |
| `GetProgressAsync` | — | none | 15s | No | `not_found` | Returns session size or turn count, which the attempt budget's progress rule needs (`docs/01` section 3.7: a resume is free only if the session grew or a commit was pushed) |
| `GetResultAsync` | — | none | 30s | No | `not_found` | **The source of truth for the outcome**: outcome, structured output, produced artifact references, session reference, usage, and the files the agent opened (from hooks or streamed tool-call output, or "unknown"). Does not block |
| `StopAsync` | — | `key` | 30s | No | — | Stopping twice succeeds. Stops the runtime; does not dispose the sandbox |

Notes:

- Spike 3 found Claude Code resume relied on state held in the worker process,
  so it failed after a worker restart (`docs/18` section 4, item 4). That is the
  failure the `ResumeAsync` contract rules out.
- A runtime without `Hooks` gets process-level control only, and the engine
  records the coarser control in the run (`docs/08` section 2.5 lists the
  fallback controls).
- **Model credentials never enter the agent's environment** (DR1): a proxy adds
  the key, or per-run keys carry a spend limit and an expiry.

### 3.1 Hook endpoint contract

The engine HTTP contract the runtime calls from inside the sandbox.

| Hook | Payload | Response | Failure behaviour |
|---|---|---|---|
| Session start | run id, step id, per-step token | Ack, and the policy for the step | Deny: the step fails with `rejected` |
| Before tool call | tool name, arguments | Allow, or deny with a reason | Deny: the tool call is skipped and reported to the runtime |
| After tool call | tool name, result summary, token usage | Ack | — |
| Session end | outcome, session reference | Ack | — |

- Authenticated with the per-step token, valid only for this run and step's
  hook endpoint (`docs/01` section 5.4, API authorisation).
- Token and cost events arrive here, so per-step cost is recorded without
  parsing transcripts (`docs/01` section 5.5, cost accounting).
- Heartbeats arrive here, so a stalled agent can be told from a slow one.
- Before-tool-call events are one source of the record of files the agent
  opened. The other is the runtime's streamed tool-call output, so the record
  does not depend on hooks (section 3).
- **Events wake, reads confirm** (`docs/04` section 1.3.1). The session-end hook
  wakes the workflow; `GetResultAsync` confirms the outcome.

### 3.1.1 Unreachable is not denied

If the engine is unreachable (network drop, engine restart), treating that as a
deny would deny every tool call, and the agent would flail or stop with a wrong
outcome.

| Case | Behaviour |
|---|---|
| **Unreachable** | The hook client in the sandbox retries with backoff and **holds the tool call** (the agent pauses) for a bounded time. Past that, the session stops in a resumable state and the step classifies it as **infrastructure** |
| **Denied** | Only when the engine answers deny, or cannot evaluate the policy. The tool call is skipped and reported to the runtime |

A hook call that fails does not fail the step, and an unreachable engine is never
a policy decision. Spike 1 made the hook endpoint unreachable mid-step: the step
retried 3 times free and completed once the endpoint returned (`docs/15` section
3.3).

---

## 4. Code host provider

### 4.1 Branches

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `CreateBranchAsync` | `PullRequest` | `key` | 30s | `conflict` (exists at a different SHA) | Creates from `fromRef`. On `timeout`, `FindByMarkerAsync`, then adopt |

### 4.2 Pull requests

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `OpenPullRequestAsync` | `PullRequest` | `key` | 30s | `conflict`, `validation_failed` | Writes the marker into the pull request body. On `timeout`, find by marker, then adopt |
| `UpdatePullRequestAsync` | `PullRequest` | none | 30s | `not_found`, `conflict` | Last write wins. Safe to repeat |
| `GetPullRequestAsync` | `PullRequest` | none | 15s | `not_found` | Returns state, head SHA, approvals, mergeability. Where the code host requires its own review, this is how the engine sees the human's approval (`docs/02` Q5) |
| `ClosePullRequestAsync` | `PullRequest` | `key` | 30s | `not_found` | Closing twice succeeds. Posts `comment` if given. Used when an abort explicitly asks for it, and after a squash merge (section 4.3) |
| `CreateRevertPullRequestAsync` | `PullRequest` | `key` | 30s | `conflict` | Serves the revert option when a delivery across several repositories only partly succeeds (`docs/01` section 7.3) |

### 4.3 Merging

There is no merge lock (`docs/20`, the merge design). At every branch level,
work **catches up** (its parent branch is merged into it), re-tests if that
changed anything, then **merges** into its parent. The merge step has two
parameters (`docs/06` section 6.5): `squash` (squash or plain merge) and
`engine_can_merge` (the engine merges, or a human merges on the code host while
the engine waits and detects it through `GetPullRequestAsync`). Typical
settings in a ticket, epic, project workflow: ticket into epic, squash, engine
merges; epic into project, plain, engine merges; project into `main`, plain, a
human merges by default (setting F1, below). The levels, and where delivery to
`main` happens, are the workflow's choice.

**Catch-up and the engine merge are VCS operations** (section 16:
`MergeBaseForwardAsync`, `MergeAsync`). This provider supplies the pull
request, protection and merge queue:

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `EnqueueMergeAsync` | `MergeQueue` | `key` | 30s | `rejected`, `not_found` | Optional path, for repositories that require a merge queue. Enqueues and returns. **It does not wait**: the engine polls `GetPullRequestAsync` or receives an inbound event |
| `GetBranchProtectionAsync` | `BranchProtection` | none | 15s | `not_found` | Read-only. The engine only checks that protection exists; it is set by an identity the engine does not hold (DR3) |

After an engine merge, a plain merge shows the pull request as merged, because
its commits reach the parent. A squash merge does not, so the engine closes the
pull request with `ClosePullRequestAsync` and a comment linking the squash
commit.

**Setting F1: the engine may not merge into the default branch.** It is a
configurable setting, no longer a fixed floor, and its default is the safe one,
so `engine_can_merge` must be false for `main` unless it is relaxed. It is set by
organisation, then repository, then workflow; the organisation can lock it
("never the engine"). Nothing an agent can edit changes it: the repository
setting is read from configuration on the default branch, never the ticket
branch, and a re-plan cannot change it. The engine enforces it at plan
validation and again at merge time, reading the default branch from the code
host (DR2). Code host protection on `main` (requiring independent review) is the
final fallback, and the engine never holds a bypass. **If the engine is allowed
to merge but the code host refuses**, that is an ordinary intervention: a
request for a human to act. As a further backstop, code host rules allow only
the engine's identity to update the integration branches below `main`.

### 4.4 Checks

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetCheckStatusAsync` | `CheckStatus` | none | 15s | `not_found` | Takes the repository as well as the SHA: a SHA alone is not unique across repositories. Must include checks that register late |

### 4.5 Review comments

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `ListReviewCommentsAsync` | `ReviewComments` | none | 15s | `not_found` | Returns threads, not flat comments, so a thread can be resolved |
| `AddReviewCommentAsync` | `ReviewComments` | `key` | 30s | `validation_failed`, `not_found` | Writes the marker. Used to post the reason a finding was declined |
| `ReplyToReviewCommentAsync` | `ReviewComments` | `key` | 30s | `not_found` | — |
| `ResolveReviewThreadAsync` | `ReviewComments` | `key` | 30s | `not_found` | Resolving twice succeeds. Closes a thread after a fix or a decline |

### 4.6 Diffs and discovery

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetDiffAsync` | `PullRequest` | none | 5 min | `not_found` | Returns an `ArtifactRef`, not the diff, so a large diff never enters workflow state |
| `FindByMarkerAsync` | `PullRequest` | none | 30s | — | Searches branches and pull requests by marker. Returns empty, not `not_found`, when nothing matches |

There is no `RequestAiReviewAsync`. An AI review on a pull request is a **review
provider** composed with the code host provider (`docs/04` section 7).

---

## 5. CI provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `TriggerAsync` | `Trigger` | `key` | 30s | `validation_failed` (unknown tier), `not_found` | Starts a run for a test tier at a commit and returns a reference. Does not wait. Spike 2's GitHub Actions dispatch returned in 22.7 s (`docs/16` section 2, check R5: a Docker tier run through CI) |
| `GetRunStatusAsync` | `Trigger` | none | 15s | `not_found` | Does not block. Must handle checks that register late, and, where a merge queue is used, report results for **queue builds** (for example the GitHub Actions `merge_group` event) |
| `ReadResultsAsync` | `Trigger` | none | 30s | `not_found`, `partial` | Structured results. `partial` means some jobs have not finished |
| `ReadLogsAsync` | `ReadLogs` | none | 5 min | `not_found` | Returns a log reference, not the log body |
| `GetArtifactsAsync` | `Artifacts` | none | 5 min | `not_found` | Returns artifact references |

Waiting is an engine mechanism. The step polls `GetRunStatusAsync` **inside one
long-running activity** (section 1.2.1), or waits for an inbound event.

---

## 6. Tracker provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `GetIssueAsync` | `Issues` | none | 15s | `not_found` | — |
| `SearchIssuesAsync` | `Issues` | none | 30s | — | Finds issues by marker (for idempotency) and lists an epic's children. Returns empty, not `not_found`, when nothing matches |
| `CreateIssueAsync` | `Issues` | `key` | 30s | `validation_failed` | Writes the marker. On `timeout`, search by marker, then adopt |
| `UpdateIssueAsync` | `Issues` | none | 30s | `not_found`, `validation_failed` | Last write wins |
| `TransitionAsync` | `Transitions` | `key` | 30s | `not_found`, `rejected` (the tracker's workflow refuses the transition) | Transitioning to the current state succeeds |
| `LinkAsync` | `Links` | `key` | 30s | `not_found`, `validation_failed` | An existing link of the same type succeeds |
| `CommentAsync` | `Comments` | `key` | 30s | `not_found` | — |

Notes:

- Agents have **no** tracker identity. Tracker writes go through engine steps
  only (`docs/01` section 4.6).
- **Field mapping is configuration, not code.** `IssueSpec` carries
  engine-level fields; the provider maps them to its own fields through its
  configuration. `CustomFields` declares support.

---

## 7. Review provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `ReviewAsync` | `Scopes[scope]`, or `Scopes` containing `"any"` | none | Set by the step | Yes | `validation_failed` (unknown check, or a `target` with neither a branch nor a document), `not_found` (input artifact or branch) | Produces structured findings for one scope |

Contract details:

- **The plug point.** Anything that reviews a change and returns structured
  findings is a review provider: the agent reviewer, an external AI review
  service, a code host's pull request review. Their findings go into the same
  ledger. Steps such as fix verification use whichever reviewer the workflow
  names. Branch levels are workflow-defined, so a provider that serves every
  level declares `Scopes` as `"any"`.
- **The version 1 reviewer is an agent on a read-only checkout** (`docs/04`
  section 7.1, from the code review design in `docs/19`). It is told the branch,
  the base and the changed files, reads the diff itself, and may open any file
  and build and run tests. It reviews against `specRefs` (the ticket's
  acceptance criteria, the epic's contract, the repository's conventions) and is
  given `machineResults`. A review of a specification document passes the
  document in `target` instead of a branch.
- **Reviewer model:** a different, ideally stronger, model than the author is
  workflow policy, not enforced by the engine. The engine records which model
  authored and which reviewed.
- **Coverage is an engine mechanism, never an engine rule.** From the record of
  files the reviewer opened (hooks or streamed tool-call output; section 3), the
  engine publishes a coverage output: changed files, files read, files not read,
  and whether the record is verified. The engine never fails a step for
  coverage on its own. If the review step sets `require_coverage` and it is not
  met, the step retries up to its attempt limit, then escalates; a route
  condition may also act on the output (`docs/06` section 8.2). The reviewer can
  run on any runtime.
- **Bug findings carry evidence**: a throwaway failing test where possible.
  Demonstrated findings block; reasoned ones are labelled and ranked lower.
- **`priorFindings` is required.** They come from the findings ledger, which is
  the source of truth over any single pass (`docs/17` section 6.5, position L1).
  Without them a verdict can change between passes on unchanged input (DR4:
  verdicts are unstable with a real model). A provider that ignores them does
  not conform. At any level above the lowest they include the children's
  ledgers (DR11, stated for epics).
- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet, **never on line numbers** (`docs/01` section 7.2). A provider without
  `Fingerprints` cannot guarantee a declined finding stays declined, and the
  engine records that.
- **Recursive levels.** At every level, work is reviewed against its parent
  branch: for example a ticket against its epic branch (given the ticket and the
  epic contract), an epic against its project branch, a project against `main`.
  The levels are workflow-defined; a project-level review exists when the
  workflow has a project level.
- The same provider serves the **on-demand** check in interactive sessions
  (`docs/01` section 7.4), so a check never appears for the first time after
  submission. A developer's pre-submit run matches the gated review when it uses
  the same brief, tools, model and commit.
- Fix verification passes the original finding as an input, for the same reason
  `priorFindings` is required.

---

## 8. Artifact store provider

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `PutAsync` | `ContentAddressed` | By content hash | 5 min | Yes | `validation_failed` (empty) | Immutable. Putting the same content twice returns the same reference |
| `GetAsync` | — | none | 5 min | Yes | `not_found` | — |
| `GetSignedUrlAsync` | `SignedUrls` | none | 15s | — | `capability_missing` | Short TTL only |

Notes:

- Immutable and content-addressed (`docs/02` Q2). A new version is a new hash
  plus a named pointer, and **the pointer lives in the record store** (section
  9).
- Holds specs, plans and every plan revision, diffs, transcripts, logs and
  reports. Workflow state holds references only.

---

## 9. Record store provider

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `UpsertRunAsync` | By run id | 15s | — | Last write wins |
| `UpsertStepRunAsync` | By step run id | 15s | — | Last write wins |
| `AppendTimelineAsync` | none | 15s | — | **Append-only.** Never updated or deleted |
| `RecordFindingAsync` | By **scope plus fingerprint** | 15s | — | Keyed by run, scope, repository and fingerprint: the same fingerprint can legitimately appear at ticket and epic scope, and in later runs |
| `RecordTriageAsync` | none | 15s | — | **Append-only.** One `TriageRecord` per decision, with a link to the record it supersedes. The current triage is the latest record |
| `RecordDecisionAsync` | By decision id | 15s | — | Append-only in practice: a decision is not revised |
| `RecordSteeringAsync` | By steering id | 15s | — | Append-only. Audited |
| `RecordCostAsync` | By step run id | 15s | — | Accumulates per step run |
| `UpsertDeliveryLedgerAsync` | By repository and run | 15s | — | Which repositories delivered, and their status |
| `PutPointerAsync` | By name | 15s | — | Named pointer to an artifact. Pointers live here |
| `ResolvePointerAsync` | none | 15s | `not_found` | — |
| `QueryAsync` | none | 30s | `validation_failed` (unknown query name) | Named queries only. The catalogue lives in the engine, in one place |

Notes:

- Relational, queried through the API (`docs/02` Q3). This is the long-term
  audit record (`docs/02` Q13).
- **Triage is append-only**, but a finding is still triaged once in effect
  (decision D8, the merge bar: no unresolved blocking finding, and every finding
  fixed or declined with a reason). A declined finding stays declined until the
  code its fingerprint points at changes. The history of decisions is kept
  rather than a re-triage being rejected with `conflict`.
- "Fixed" is sticky, enforced by the engine: when a fix is verified the ledger
  records the location and that code's hash, and a later finding there is
  recorded as a re-raise, not acted on, while the hash is unchanged (`docs/17`
  section 6.5, position L2).
- The workflow reads the delivery ledger through an activity, never directly
  (`docs/01` section 7.3).
- `org_id` is part of the key from version 1 (`docs/02` Q15).
- **Query catalogue ownership:** a distribution (an organisation's own package
  of providers and configuration, `docs/01` section 4.5) may add **read-only**
  named queries over published views, shipped in its own package. No
  third-party schema changes in version 1.

---

## 10. Secrets provider

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `ResolveAsync` | none | 15s | `not_found`, `forbidden` | Returns the value. Configuration refers to secrets and never contains them. Values are scrubbed at the logging layer |

---

## 11. Notifications provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `SendAsync` | `Channels[channel]` | `key` | 30s | `validation_failed` (unknown channel), `not_found` | Fire and forget. A failure is infrastructure and retries |
| `PostDecisionRequestAsync` | `Interactive` | `key` | 30s | `capability_missing`, `not_found` | Posts the request and returns a message reference. **Does not wait for the answer** |

The answer arrives through the inbound gateway (`docs/04` section 1.3.2) and
becomes an **API decision call**, the same path as any other decision (`docs/02`
Q4). The policy model states which decisions may be made from chat (`docs/01`
section 5.4).

---

## 12. Credential broker

| Method | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|
| `IssueAsync` | none | 15s | `forbidden` (scope not grantable), `validation_failed` | Returns a short-lived credential for an actor, target system and scope. **Refuses a request wider than it may grant; never silently narrows it**, because a narrowed grant looks correct at the call site |
| `RefreshAsync` | none | 15s | `forbidden`, `not_found` (expired beyond refresh) | — |

Notes:

- **No provider reads credentials from configuration** (`docs/01` section 4.6,
  credential broker).
- **Agents get read-only code host credentials, per step, never one that can
  push** (DR13). Scope: the run's repository, plus any other repositories the
  workflow declares the agent may read (for example to verify a contract).
- **Model credentials never enter the agent's environment** (DR1).
- Measured in Spike 2: a one-repository token expired after 60 minutes, with the
  expiry read from the remote rather than computed; a wider access level,
  another repository and a TTL over the maximum were all refused (`docs/16`
  section 2, check R6: short-lived, scoped credentials).

---

## 13. Session surface provider

| Method | Requires | Idempotency | Timeout | Errors | Semantics |
|---|---|---|---|---|---|
| `PrepareAsync` | — | `key` | 5 min | `validation_failed` | Prepares the workspace for a human's interactive session and returns how the human reaches it |

The surface **only** prepares the workspace. Everything else is an API action,
not a provider method:

- Submission comes from the human's CLI or MCP server through the API and
  becomes a Temporal Update (a message the workflow validates and answers).
- Checks are run by the engine (validators and review providers), not by the
  surface. The human's CLI or MCP server asks the API, and the engine runs the
  check.
- A "merge when ready" tool, if offered, only asks the engine to run the merge
  step (`docs/20`).

Hooks and policy do not apply in the human's environment. The trust boundary is
the submission, and the human is the accountable actor in the audit record.

---

## 14. Cross-cutting rules

| Rule | Detail |
|---|---|
| Capability check | The caller checks before use and fails with a named missing capability. It never assumes success |
| Credentials | Through the broker only. Agents hold read-only code host credentials, never one that can push (DR13), and no model credential (DR1) |
| Mechanism, not policy | The engine provides mechanisms and guardrails; the workflow decides (branch levels, review bases, where delivery happens) |
| Success mapping | Every documented success response is mapped; an unmapped one is `unexpected_response`, a work error (DR8; section 1.5) |
| Observability | The engine wraps every call, so providers get spans, timeouts and error classification without extra code. Providers only add attributes |
| Contract tests | One suite per interface, shipped with the engine (`docs/02` S5). A provider conforms only if it passes |
| No waiting | No method waits on human or external completion |
| Timeout ambiguity | On `timeout` from a create method, find by marker before retrying (section 1.4) |

---

## 15. Open questions

None in this document. The open questions on the interfaces are in `docs/04`
section 16.

---

## 16. VCS provider

The version-control operations the engine performs (`docs/04` section 17).
Version 1 implements Git only; the interface is VCS-neutral. Agents never call
this provider; it uses the engine's own credential from the broker.

| Method | Requires | Idempotency | Timeout | Heartbeat | Errors | Semantics |
|---|---|---|---|---|---|---|
| `GetRevisionAsync` | — | none | 15s | No | `not_found` (working copy) | The working copy's current revision in the sandbox |
| `CommitAllAsync` | — | none | 30s | No | `not_found` | Commits any uncommitted changes with the given message and returns the new revision, or null if there was nothing to commit. Repeating it with nothing left to commit returns null |
| `FetchCommitsAsync` | — | none | 5 min | Yes | `not_found` | Returns the commits since `sinceRevision` as a `CommitBundle` (an artifact reference plus the head revision). Reads only |
| `PushAsync` | — | none | 5 min | Yes | `base_moved` (the reference is no longer at `expectedOldRevision`) | Moves a branch or hidden reference to `revision`, sending the bundle if needed. A push, never a merge. Pushing a reference to the revision it already holds succeeds |
| `MergeBaseForwardAsync` | — | none | 30s | No | `conflict`, `base_moved` | The catch-up. Merges `branch` (the parent, merged **from**) into `into` (the child, merged **into**). **`MergeResult` states whether `into` changed, and its new head**: tests re-run only if it changed. "Nothing to merge" is a completed merge with the branch unchanged (DR8; Spike 3's GitHub merge API answered it with 204 and no body, `docs/18` section 4, item 2). A `conflict` routes to an agent step that resolves it, not to a human first (`docs/06` section 6.2) |
| `MergeAsync` | `Squash` when `squash` is true | `key` | 30s | No | `base_moved`, `conflict`, `rejected` (protection or policy) | The engine merge. **Builds the merge commit, or the squash commit if `squash`, from exactly `testedRevision` and pushes it to `target`.** If `target` is no longer `expectedTargetRevision` the push is refused with `base_moved`: the step routes back to catch-up, within its attempt allowance (default 3, counting every attempt; configurable at every level). On `timeout`, read `target` first: if it already contains the tested change, the merge happened (section 1.4) |

### 16.1 Commits and pushes during an agent step

The full sequence is in `docs/04` section 17.1. The contract points:

- **Detection is through this provider, not the agent runtime.** At each
  check-in on a running step (about every 30 seconds) the engine calls
  `GetRevisionAsync`; if the revision moved it fetches and pushes. Hooks and the
  optional `checkpoint` tool may trigger an immediate push; nothing depends on
  them.
- **While the step runs, pushes go to the hidden work-in-progress reference**, so
  they trigger no CI and expose no half-finished work.
- **When the step succeeds**, `PushAsync` moves the ticket branch to the final
  commit, once. A failed step's commits stay on the hidden reference for a retry.
- **Uncommitted changes at the end of the step** are committed by default with
  `CommitAllAsync` ("left uncommitted by the agent"); a workflow can make the
  step fail instead.
- A role with no write access pushes nothing, and commits made anyway fail the
  step. A step whose `push` parameter is false pushes nothing.
