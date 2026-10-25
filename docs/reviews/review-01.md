# Review 01: feedback on the first-pass architecture

Date: 2026-10-26. Reviewer: product owner (via a review assistant).
Reviewed: `docs/01-architecture-first-pass.md`, `docs/02-working-questions.md`, `HANDOVER.md` at commit a9defe1.

## Summary

Good first pass. Keep these; they are the right calls:
- Task queues by capability (S1).
- One `applyPatch` path for re-plans and steering (S2).
- Configuration precedence with safety floors (DP6).
- Idempotency markers on remote objects (S4).
- Fingerprints that never use line numbers (S6).
- No second execution backend (DP9).
- Epic-level review on the aggregate diff (DP10).

The points below need changes. Answers to the must-settle questions (Q1-Q6) will follow in separate feedback files as they are decided.

## Changes requested

| # | Issue | Where | Change |
|---|---|---|---|
| F1 | The language comparison uses the enterprise adopter's Windows-only .NET Framework repositories as an argument for a .NET engine. The engine does not build target repositories; sandboxes do. Those repositories need a Windows sandbox (S1 already covers this), not a .NET engine | DP2 | Remove this argument. Compare on: Temporal SDK, agent integration (see F3), typing, contributor pool, the adopter's supported platforms |
| F2 | DP2 states the Claude Agent SDK has strong .NET support. It does not: the Claude Agent SDK is TypeScript and Python only. .NET has the Claude API client only, or must run the Claude Code CLI as a subprocess. The GitHub Copilot SDK does support .NET | DP2 | Correct the table |
| F3 | Where the agent loop runs is not stated. Decided (product owner, 2026-10-26): **pattern C, "agent in sandbox with engine hooks"**. Record it as an architectural pattern in the architecture document. See the section below | 1.1, 6.1 | Add the pattern; settle it before Q1, because it makes the engine language largely independent of agent SDK languages |
| F4 | A long agent step (an hour or more) that loses its worker or sandbox restarts from scratch. Interruptions were the prototype's main operational pain | 3, 7.1 | Add resumable agent steps: work is checkpointed (work-in-progress commits pushed to the branch, agent session reference in the artifact store) so a retry continues rather than restarts. Consider letting the sandbox outlive a worker crash so a retry re-attaches to it |
| F5 | The interpreter loop is sequential and the plan shape has no fan-out or join, but A11 says several tickets of one epic may run concurrently | 3.1, 3.3, A11 | Add parallel branches and join semantics to the plan shape and the loop |
| F6 | Cost controls are under "Later" (Q16). The prototype ran for 27 hours with no record of token or compute cost | Q16 | Move to "Should settle". At minimum, record tokens, model and compute time per step from the first version; budgets can come later |
| F7 | Section 8 cites A13 for the deployment assumption; A13 is "the API is the only way humans interact". The deployment assumption is A7 | 01, section 8 | Fix the reference |

## Decided pattern: agent in sandbox with engine hooks (F3)

Three options were compared for where the agent loop runs:

| Option | Description | Verdict |
|---|---|---|
| A | Agent CLI or SDK runs inside the sandbox; the agent worker starts it and reads its output | Base of the chosen pattern |
| B | Agent loop runs in the agent worker; each tool call is executed remotely in the sandbox | Rejected, not deferred. See reasons below |
| C | A, plus the agent runtime's hooks (before and after each tool call, session start and end) call back to the engine | **Chosen** |

Pattern C:
- The agent runtime (for example Claude Code, GitHub Copilot CLI) runs inside the sandbox with its own tools, unchanged.
- Runtime hooks call an engine endpoint for: policy checks on each tool call (deny writes for read-only roles, deny pushes to protected branches, deny changes to `.git/config` and similar), structured events, token and cost accounting, and heartbeats.
- Resumability: work-in-progress commits pushed to the branch, plus the runtime's own session resume, with the session reference stored in the artifact store. The sandbox outlives a worker crash; a retry re-attaches to it by idempotency key.
- Model credentials: short-lived and scoped to the step, preferably through a model gateway so no long-lived key enters the sandbox.
- The agent runtime provider interface declares hook support as a capability. A runtime without hooks gets coarser control (process-level only) and the engine records that.

Why B is rejected:
- It requires building and maintaining an agent harness (context management, compaction, edit tools, sub-agents, MCP, prompt caching). Vendor harnesses are better and improve monthly; coding quality depends on the harness.
- Hundreds of tool calls per step as Temporal activities means thousands of history events per step.
- Network latency on every tool call.
- Commit-level checkpoints plus session resume are expected to be enough; measure before revisiting.

Consequence for Q1: the engine starts processes and serves hook callbacks over HTTP, so agent SDK language support is no longer a factor in the language choice.

## Decided: Q1 language is .NET

Decided by the product owner, 2026-10-26.

Reasons:
- Strong typing across a large provider surface and for an organisation maintaining a fork.
- Supported by the first enterprise adopter, with worker templates available.
- With pattern C, agent SDK language support is not a factor.
- A self-contained single-file binary is possible for the CLI.

Condition: the main risk is non-determinism in workflow code, which the .NET SDK does not catch at runtime the way the Python sandbox does. Make replay tests in CI (S8) mandatory from the first workflow, and add a code review rule for workflow code (no `Task.Run`, no wall clock, no unordered collections, no I/O).

Update DP2 and close Q1.

## Decided: storage model (Q2, Q3, Q10, and Q13)

Decided by the product owner, 2026-10-26.

| Store | Contents | Technology by profile | Model |
|---|---|---|---|
| Temporal history | Working state only: references and small summaries | Temporal | Transient; deleted after namespace retention |
| Artifact store | Specs, plans and every plan revision, diffs, transcripts, logs, reports | Local filesystem (solo); S3 or Azure Blob | Immutable, content-addressed (Q2). A new version is a new hash plus a named pointer in the record store |
| Record store | Runs, step runs, findings ledger, decisions, steering log, timeline events, cost per step | SQLite (solo); Postgres | Relational, queried through the API (Q3). Timeline append-only. The long-term audit record (answers Q13) |

Git is not a store (Q10). Publishing artifacts to a repository for human review is an engine step (`artifact.publish`). No requirement to browse run state through Git; the console and API replace it.

## Decided: Q4 decisions, steering and API authorisation

Decided by the product owner, 2026-10-26.

Transport:
- Decisions and steering use Temporal **Updates**, not Signals. Each carries the gate id and plan revision it applies to; the update validator rejects stale or invalid requests (no open gate, plan revision changed). The API retries against the current run after continue-as-new (mitigates R3).
- Reads do not use Temporal queries. The console and API read from the record store.

Authentication:
| Caller | Method |
|---|---|
| Humans (console, CLI) | OIDC against the organisation's identity provider; a local token in the solo profile |
| Automation | Service-account API tokens |
| Agent hooks from the sandbox (pattern C) | Short-lived token issued per step, scoped to that run and step's hook endpoint |

Authorisation:
- Roles from the policy model (viewer, approver, steerer, admin), mapped to identity-provider groups, scoped by team or repository.
- Only the API authorises and writes the audit record. The workflow trusts the API (DP8). Only the API and workers can reach the Temporal namespace.

Chat integration (Slack first; Microsoft Teams later through the same interface):
- Notifications: decision needed, run stuck, run finished. Required.
- Approvals from chat: wanted for some decisions. A chat action calls the API like any other client. The chat user is mapped to an identity-provider identity (for example by verified email). The policy model states which decisions may be made from chat; for example, ticket-level gates yes, merge into a default branch only in the console. The notifications provider's `Interactive` capability declares support.

Chat must be a pluggable provider: Slack first, with others (Microsoft Teams, WhatsApp, Discord, and so on) swappable later through the same notifications/chat interface. No Slack-specific types outside the Slack provider.

## Decided: Q5 identity

Decided by the product owner, 2026-10-26. Treat as a first design in uncharted territory: keep it behind an interface so the mechanism can change.

| Actor | Identity |
|---|---|
| Engine against the code host | A GitHub App, or the equivalent bot identity on other hosts |
| Agents in the sandbox | A token minted per step from the app identity: one repository, minimum permissions (contents write), about one hour; refreshable through the hook endpoint |
| Engine against the tracker | A service account; every action records "on behalf of" the run's requester or approver |
| Agents against the tracker | None. Tracker writes only through engine steps |
| Solo profile | The developer's own token for both engine and agents |

Merges into a default branch: the app cannot approve its own PR. Where branch protection requires a code host review, the human approves on the code host and the engine waits for it; otherwise the human approves in the console or chat and the app merges. The audit record shows the bot as actor and the human as approver.

Flexibility requirement:
- Add a **credential broker** interface: given an actor (engine, agent step), a target system and a scope, it returns a short-lived credential. GitHub App tokens, service accounts, personal tokens and future mechanisms (for example OIDC workload identity) are implementations.
- No provider reads credentials directly from configuration; all go through the broker.
- Code host, tracker, CI and chat providers remain fully swappable; identity choices must not leak into their interfaces.

## Decided: Q6 provider packaging

Decided by the product owner, 2026-10-26.

- Version 1: providers are in-process .NET packages (NuGet). A host project references the providers it ships; configuration selects them by name.
- Provider interfaces must be transport-neutral: async, plain serialisable data objects, no third-party SDK types in signatures, cancellation and classified errors on every call. This keeps a generic **remote provider** adapter possible later (implements any interface by calling an out-of-process provider over gRPC or HTTP), for providers in other languages or needing isolation. Do not build the adapter in version 1.
- Organisation customisation uses a **distribution repository**, not a fork: a separate repository with the organisation's own providers and a small host project that builds its worker and API images, plus its configuration. A fork is needed only for core changes, which should be rare. Design the packages and host so this works (published packages, documented host composition).

## Status of the must-settle questions

| Question | Status |
|---|---|
| F3 agent loop location | Decided: pattern C |
| Q1 language | Decided: .NET |
| Q2, Q3, Q10 (+Q13) storage | Decided |
| Q4 decisions, steering, API auth | Decided, plus pluggable chat |
| Q5 identity | Decided, behind a credential broker |
| Q6 provider packaging | Decided: in-process now, transport-neutral interfaces, distribution repository |

Next per the suggested order of work: provider interfaces and the step catalogue. Feedback on the should-settle questions (Q7-Q14) will follow in a later review file.
