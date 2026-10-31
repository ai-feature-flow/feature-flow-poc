# Review 05: feedback on the provider interfaces (first pass)

Date: 2026-10-26. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/04-provider-interfaces.md` at commit 4f5753c.

## Summary

The shape is sound: shared rules in section 1, typed capabilities, transport-neutral signatures, the credential broker, and the session surface all follow the decisions. The main problem is that several methods **block on external or human time** inside a provider call. That conflicts with how decisions reach the workflow (Temporal Updates through the API, review 01 Q4). Fixing that is the first change; the rest are gaps.

## Changes requested

### P1. No provider method waits on human or external time

| Method | Problem | Change |
|---|---|---|
| `INotificationsProvider.AskAsync` returns `DecisionAnswer` | A human may answer days later. The answer must arrive through the API as an Update, like every other decision (Q4, chat approvals) | Replace with `PostDecisionRequestAsync` returning a message reference. The chat platform's callback goes to an inbound endpoint, which the provider translates into an API decision call |
| `ISessionSurfaceProvider.WaitForSubmissionAsync` | Same: submission comes from the human's CLI or MCP through the API | Remove. Submission is an API action that becomes an Update |
| `ISessionSurfaceProvider.RunCheckAsync` | Checks are run by the engine (validators, review providers), not by the surface. In surface A the human's CLI or MCP calls the API, and the engine runs the check | Move to an API action. The surface only prepares the workspace and tells the human how to reach it |
| `ICiProvider.WaitForRunAsync` | Waiting is an engine mechanism (durable timer and polling, or an event), not a provider method | Replace with non-blocking `GetRunStatusAsync`. Optional capability for push events (webhooks) |

General rule to add to section 1: **provider methods are short, bounded calls.** Waiting is done by the engine with durable timers, polling, or inbound events. Providers that can push events declare an `InboundEvents` capability and translate incoming webhooks into engine events through the API.

### P2. Gaps in the code host interface

| Missing | Needed for |
|---|---|
| `GetPullRequestAsync` (state, head SHA, approvals, mergeability) | "Human approves on the code host and the engine waits" (Q5), merge readiness |
| `ListReviewCommentsAsync`, `ResolveReviewThreadAsync` | Ingesting AI review comments into the findings ledger; closing threads after fix or decline |
| `GetDiffAsync` (between two refs) | Ticket and epic aggregate diff for review (DP10) |
| `FindByMarkerAsync` (branches, PRs) | Idempotent adoption of existing objects (S4) |
| `ClosePullRequestAsync`, `CreateRevertPullRequestAsync` | Abort when explicitly asked; the revert option of a partial delivery (Q14) |
| `RepoRef` on `GetCheckStatusAsync` | A SHA alone is not unique across repositories |

Remove `RequestAiReviewAsync` from the code host. An AI review on a PR (for example Copilot review) is a **review provider** implementation that uses the code host provider to request the review and read the comments. That also answers open question 4 for this case (see P8).

### P3. Gaps in the agent runtime interface

- No way to get the result. Add `GetResultAsync(session)`: outcome, structured output, produced artifact references, session reference, usage. The session-end hook is a signal, not the source of truth.
- `ResumeAsync` needs the sandbox handle as well as the session reference.

### P4. Gaps in the sandbox interface

- `CreateAsync` must say what to check out: repository, branch, and the credential to use (from the broker).
- Add file transfer: put input artifacts into the sandbox, get output files out (`CopyInAsync`, `CopyOutAsync`, or mounts declared at creation).
- `ExecuteAsync` for long commands needs output streaming or periodic progress for heartbeats (the prototype lost steps to idle timeouts).

### P5. Review provider needs history

Add the prior findings for the same scope (from the ledger) as an input. The prototype's spec check was blind to its earlier findings and its verdicts changed between passes. Fix verification also needs the original finding as an input.

### P6. Tracker gaps

- `SearchIssuesAsync`: find existing issues by marker (idempotency) and list an epic's children.
- Field mapping: organisations use custom fields (story points, components, team fields). Define how `IssueSpec` maps to provider-specific fields through provider configuration, not code.

### P7. Artifact store pointers contradict the storage decision

Section 8 has `PutPointerAsync` and `ResolveAsync` on the artifact store, but its own note and the storage decision put pointers in the record store. Remove them from the artifact store; add pointer methods to the record store.

## Answers to section 16

| # | Question | Answer |
|---|---|---|
| 1 | `ProviderResult<T>` vs exceptions | Keep `ProviderResult<T>` for expected outcomes (classified errors). Exceptions only for programming errors and bugs. The engine's decorator turns an unexpected exception into an infrastructure error with a distinct reason so it is visible |
| 2 | Named queries vs query object | Named queries, with the catalogue in the engine (one place) |
| 3 | Split `ExecuteAsync` | Keep one method. Agent runtimes and test tiers both run commands; the difference is the command and its limits |
| 4 | Provider-to-provider capability dependencies | Add a `Requires` list to `ProviderDescriptor` (for example "code host with `ReviewComments`"). Run-start capability negotiation checks it. Prefer composition: a provider that needs another receives it through dependency injection, as in the AI review case in P2 |
| 5 | Is the credential broker a provider? | Yes in the sense that it is swappable, but it is not step-facing. Split the catalogue into two groups: **step providers** (used by engine steps: sandbox, agent runtime, code host, CI, tracker, review, notifications, session surface) and **platform services** (used by the engine itself: credential broker, secrets, artifact store, record store). Same rules for both |
