# Review 06: feedback on the provider contracts and the step catalogue

Date: 2026-10-26. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/04-provider-interfaces.md` (review 05 applied), `docs/05-provider-contracts.md`, `docs/06-step-catalogue.md` at commit d015ea0.

## Summary

Review 05 is fully applied. The contracts are thorough: the timeout ambiguity rule (find by marker before retrying a create) and "the result comes from `GetResultAsync`, the hook is a signal" are both right. The catalogue covers every area in the brief.

Two contract changes matter most: long sandbox commands still block inside a provider call (C1), and an unreachable hook endpoint would deny every tool call (C2). Both repeat the prototype's network-drop failure in a new form.

A general rule answers several open questions at once: **events wake, reads confirm.** Hooks, webhooks and chat callbacks only wake the workflow. The workflow then reads the truth through a short provider call (`GetResultAsync`, `GetRunStatusAsync`, `GetPullRequestAsync`). Human decisions remain Updates through the API.

---

## Provider contracts

| # | Issue | Change |
|---|---|---|
| C1 | `ISandboxProvider.ExecuteAsync` runs a command for a step-defined time (possibly hours) inside one provider call. That breaks the "provider calls are short and bounded" rule, and a worker crash loses the command even when the sandbox survives | Replace with **start and poll**: `StartCommandAsync` returns a command handle; `GetCommandStatusAsync` returns status, exit code, and output since a cursor; `CancelCommandAsync`. Same pattern as the agent runtime. A retry after a worker crash re-attaches to the sandbox and **re-attaches to the running command** by handle. This also answers contracts open question 2 (`IProgress` is not needed) |
| C2 | Hook endpoint: "a policy check that cannot be evaluated is treated as a deny". If the engine is unreachable (network drop, engine restart), every tool call is denied and the agent flails or gives up with a wrong outcome | Distinguish **unreachable** from **denied**. Unreachable: the hook client in the sandbox retries with backoff and holds the tool call (the agent pauses) for a bounded time; past that, the session stops in a resumable state and the step classifies it as infrastructure. Deny only when the engine answers deny, or when the engine itself cannot evaluate the policy |
| C3 | `MergeBaseForwardAsync` returns `MergeResult`, but review 03 decided "rerun checks only if the merge forward changed the ticket branch" | `MergeResult` must state whether the branch changed (and the new head SHA) |
| C4 | `RecordFindingAsync` is keyed by fingerprint alone and returns `conflict` on re-triage | Key findings by scope (run, scope, repository) plus fingerprint: the same fingerprint can legitimately appear at ticket and epic scope, and in later runs. Make triage **append-only**: a `TriageRecord` per decision, with a supersedes link. The current triage is the latest record. This answers contracts open question 4 and keeps the audit trail |
| C5 | Inbound events endpoint undefined (contracts open question 1) | One inbound gateway in the API. Per provider: verify the webhook signature, deduplicate by the remote delivery id, translate to an engine event. Engine events are delivered to the workflow as Signals that **only wake it**; the workflow then reads state through the provider. Human decisions stay Updates through the API |
| C6 | Should `GetResultAsync` block (contracts open question 3) | No. The session-end hook is a wake-up event; `GetResultAsync` stays the source of truth; polling is the fallback if the event is lost |
| C7 | Query catalogue ownership (contracts open question 5) | A distribution may add **read-only** named queries over published views, shipped in its own package. No third-party schema changes in version 1 |

## Step catalogue

| # | Issue | Change |
|---|---|---|
| S1 | `fix.verify`: "a fix must add a regression test; without one the step fails". Some findings cannot sensibly have one (log message content, OpenAPI metadata, documentation) | Keep the default, but allow "regression test not applicable" with a recorded reason. The verification review judges whether the reason is acceptable; an unjustified reason fails the step |
| S2 | `delivery.deploy` "requires whatever the deployment system needs", but there is no deployment provider. In practice delivery is merging delivery PRs in order; many organisations deploy on merge | Version 1: `delivery.deploy` = merge the repository's delivery PR, then optionally `wait.for_state` on a deployment status the code host or CI reports. No deployment provider in version 1. Add one later only if workflows need to trigger deploys directly |
| S3 | Merge lock scope (catalogue open question 3) | Lock per **target branch** (repository plus branch). Integration branches of different epics in the same repository do not conflict. Implement as a Temporal-native mutex (for example a lock workflow per target branch), not an external lock |
| S4 | No step for workflows to send a notification (for example "epic delivered" or "spec ready for review") | Add `notify.send` over `INotificationsProvider.SendAsync` |
| S5 | No step for tracker comments, though the provider has `CommentAsync` | Add `tracker.comment` |
| S6 | `human.gate` says the API evaluates the `steering` policy for a decision | A gate decision is evaluated against the `approval` policy. `steering` is for amendments, overrides, extra attempts and abort |

## Answers to catalogue section 15

| # | Question | Answer |
|---|---|---|
| 1 | `agent.review` as a separate kind | Drop it. One `agent.run`; the plan states `write_access: none`, which keeps the floor visible. Roles are workflow content; the engine enforces `write_access` (mechanism vs policy) |
| 2 | Should `scm.revert` merge automatically after approval | No. Always gate. Reverts usually target a default branch, which is a safety floor anyway |
| 3 | Merge lock scope | Per target branch (S3) |
| 4 | `test.run_tiers` one step or per tier | One step taking a list of tiers. Tiers with the same execution target share a sandbox; results are reported per tier. A Docker-dependent tier delegated to CI runs through the CI provider inside the same step |
| 5 | `wait.for_state` vs `ci.wait_checks` | Keep both. `ci.wait_checks` carries specific semantics (late-registering checks, required check names, a grace period before deciding there is no CI) |
| 6 | Default attempt allowances | Accepted as starting values. Tune from the recorded attempt and cost data |
