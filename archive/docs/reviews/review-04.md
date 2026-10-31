# Review 04: decisions on the remaining "Later" questions

Date: 2026-10-26. From: product owner (via a review assistant). To: architecture.
Covers Q15, Q17-Q20 from `docs/02-working-questions.md`.

## Q15. Multi-tenancy

Version 1: one organisation per installation (A10 holds). Teams are scoped by the policy model and roles. Add an `org_id` to record store keys from the first version (cheap now, expensive later). No other multi-tenant features.

## Q18. Disaster recovery

No custom DR mechanism.
- Record store: standard database backups with point-in-time restore.
- Artifact store: cloud versioning and replication; content addressing makes integrity checks simple.
- Temporal unavailable: already handled (section 3.5).
- Add a reconcile job that rebuilds the run index in the record store from Temporal for in-flight runs.
- Document recovery point and recovery time per profile.

## Q19. `feature-flow.yaml` changes mid-run

- Each step reads `feature-flow.yaml` from its branch when it starts. Nothing is copied into the plan (the prototype went stale exactly this way).
- Each step records the hash of the resolved manifest it used.
- If a change removes a capability the run needs, run capability negotiation again before the next step.
- Agent edits to `feature-flow.yaml` are flagged at review. Whether they need approval is workflow policy, not an engine concern.

## Q20. Interactive human sessions

An interactive session is an engine mechanism: prepare a workspace (branch, drafts, context, prompt), wait for a submission, validate it, continue. Where the human works is a pluggable **session surface**.

| Surface | Status |
|---|---|
| A. Hand-off to the human's own agent tool (VS Code with Copilot, Claude Code, Copilot CLI) on their own machine | **Version 1 (MVP)** |
| B. Review rounds on a PR in the planning repository | Later, optional |
| C. Chat in the console with the agent in a sandbox | Later, optional |
| D. Short clarification questions in a chat platform thread | Later, optional |

Why A is the MVP: the human works in an unsandboxed agent with full access to their environment: other repositories, discovery, enterprise systems (wikis, observability, ticketing). That is what design and specification work needs, and a sandbox cannot provide it.

Requirements for A:
- A CLI (and an MCP server for the human's agent) to fetch the session workspace, run checks, and submit. Authenticated as the human (OIDC, Q4).
- Hooks and policy do not apply in the human's environment. The trust boundary is the submission: the engine validates what is submitted, and the human is the accountable actor in the audit record.
- **Pre-submit checks:** the session can run the same checks that later steps will apply (for example the plan or verification-requirement review) on demand, before submitting. In the prototype, a spec session took 8 rounds mostly because agent-drafted verification requirements were accepted without detailed review and then failed the later plan review. Running that review inside the session removes those round trips. This is the same principle as code review: no check appears for the first time after the work is submitted.
  - Engine side: expose any validator or review check as an on-demand action for a session, with the same structured findings as the gated step.
  - Workflow side: which checks a session should run before submitting is library content.

## Q17. Observability contract

| Area | Decision |
|---|---|
| Standard | OpenTelemetry for traces, metrics and logs, with a configurable exporter (OTLP to any backend). No custom observability interface |
| Traces | One trace per run; spans per step, activity and provider call, with run id, step id and error class as attributes. Use the Temporal .NET SDK's OpenTelemetry support |
| Provider contract | The engine wraps every provider call (decorator), so providers get spans, timeouts and error classification without extra code. Providers only add attributes |
| Standard metrics | Step duration, attempts, infrastructure retries, schedule-to-start latency per task queue (shows missing or stuck workers), tokens and cost, findings by severity, human wait time, sandbox start time |
| Stuck runs | Detect by heartbeat age and by the Q12 progress rule; send a "run stuck" notification (S13) |
| Logs | Secrets scrubbed at the logging layer. Agent transcripts are artifacts, not logs |

## Next

All working-list questions now have a decision. Proceed with the provider interfaces and the step catalogue, including:
- The session step and session surface interface (Q20), with the session CLI and MCP actions: fetch workspace, run checks on demand, submit.
- The on-demand check action, sharing validators and structured findings with the gated steps.
