# Working list: assumptions, suggestions, discussion points, open questions

Status: for interactive discussion. Date: 2026-10-25. Updated 2026-10-26 for review 01.
Companion to `docs/01-architecture-first-pass.md`.

Review 01 (`docs/reviews/review-01.md`) decided Q1-Q6, Q10 and Q13, and settled
where the agent loop runs (DP11). Those are recorded under "Decided" below and
applied in the architecture document. Q7-Q9, Q11, Q12, Q14 and Q16 remain open.

How to read this:

- **A** = assumption I made to get the first pass written. Correct me if wrong.
- **S** = suggestion, a design choice I would make now.
- **DP** = discussion point, a real fork in the road. I have a recommendation.
- **Q** = open question, I do not yet have a recommendation.

Priority: **Must settle** blocks other design work. **Should settle** blocks the step catalogue. **Later** can wait.

---

## A. Assumptions

| # | Assumption | If wrong |
|---|---|---|
| A1 | A run maps to exactly one Temporal workflow execution; a ticket inside an epic is a child workflow | Changes the composition model and the epic-level review design |
| A2 | The engine never stores code, diffs or transcripts in workflow state; references only | Changes the artifact store design and history budget |
| A3 | Human waits are unbounded. A run may sit at a gate for days | Changes timeout and reminder design |
| A4 | The engine is the only writer to the branches and PRs it creates | Changes conflict handling and merge design |
| A5 | One repository per ticket is the common case; multi-repository tickets are supported but rarer | Changes the default plan shape |
| A6 | Agents work on a branch; the engine owns branch creation, PRs and merges | Changes the source control step set |
| A7 | "Deploy" means trigger and gate an external deployment system, not perform it | Adds a whole subsystem |
| A8 | The record store is queryable (SQL-like), not just a blob store | Changes the console and reporting design |
| A9 | Providers are trusted code. No sandboxing of a provider itself | Changes the packaging model (see Q7) |
| A10 | A run is single-tenant: one organisation, one configuration | Changes the API and namespace model |
| A11 | The engine may run several tickets of one epic concurrently | Changes the integration branch design (see Q11) |
| A12 | Findings are scoped to a repository and a diff, and can be re-scoped to an epic or delivery | Changes the findings ledger schema |
| A13 | The API is the only way humans interact with a run. No direct Temporal access | Changes the authorisation model |
| A14 | Interpreter versioning uses Temporal's patching API; plan schema versioning is separate | Changes the versioning strategy |

---

## S. Suggestions

| # | Suggestion | Why |
|---|---|---|
| S1 | Route agent steps to task queues by capability (`feature-flow-agent:os-windows`, `feature-flow-agent:docker`) | Handles Windows-only repos and Docker-dependent tiers without special cases in the interpreter |
| S2 | One `applyPatch` function for re-plans and steering, with protected steps enforced there | Single audit and validation path; closes the "re-plan removed a protected step" hole |
| S3 | Capability negotiation at run start, not per step | A run fails in the first minute, not at step 40 |
| S4 | Deterministic idempotency key plus hidden markers on every remote object the engine creates | Retries adopt existing PRs and branches instead of duplicating them |
| S5 | Contract test suite per provider interface, shipped with the engine | A forking organisation can prove its provider conforms |
| S6 | Fingerprint findings on rule, repo, path, symbol and normalised snippet. Never line numbers | Declined findings stay declined until the code actually changes |
| S7 | Full finding bodies in the record store; capped summary in workflow state | Keeps history small without losing the ledger |
| S8 | Replay tests in CI: recorded histories replayed against a changed interpreter | The only reliable way to catch non-determinism |
| S9 | Classify `unauthenticated` and `forbidden` as non-retryable after a short budget | Almost always a configuration fault; retrying hides it |
| S10 | Validate configuration at startup and again at run start | Fails fast, with a clear message |
| S11 | Short-lived, least-privilege, per-step credentials for sandboxes | Limits blast radius of a compromised or confused agent |
| S12 | Treat CI delegation as a first-class path, not a fallback | The prototype's worst problems came from sandbox and CI divergence |
| S13 | Notifications for "run stuck" as well as "decision needed" | The prototype stalled silently |
| S14 | Version the plan schema separately from the interpreter | Workflow content can evolve without interpreter changes |

---

## DP. Discussion points

### DP1. Project name — settled
**Feature Flow.** `feature-flow` in code, configuration, paths and package names. This repository is `feature-flow-poc`: a proof of concept that may evolve into the finished product or be rewritten.
*Note:* the name is settled, so DP1 is closed. The open consequence is naming for the repository manifest directory (`.feature-flow/`) and for Temporal namespaces and task queues, which should follow the same prefix.

### DP2. Language: .NET or Python (brief Q1) — settled
| | .NET | Python |
|---|---|---|
| Temporal SDK | First-class, well maintained | First-class, well maintained |
| Agent integration | Not a factor under pattern C (DP11): the engine starts the agent runtime and serves hook callbacks over HTTP. Where it does matter, the GitHub Copilot SDK supports .NET; the Claude Agent SDK is TypeScript and Python only, so .NET would use the Claude API client or run the Claude Code CLI as a subprocess | Full Claude Agent SDK and Copilot SDK support |
| Typing | Strong typing across a large provider surface and for an organisation maintaining a distribution | Weaker compile-time guarantees |
| Contributor appeal | Smaller open-source contributor pool for this domain | Larger AI/ML contributor pool |
| Adopter fit | Supported by the first enterprise adopter, with worker templates available | Not the adopter's platform |
| Packaging | Self-contained single-file binary possible for the CLI | Requires a runtime on the host |

*Decided (product owner, 2026-10-26):* **.NET.** Reasons: strong typing across a large provider surface and for an organisation maintaining a distribution; supported by the first enterprise adopter with worker templates available; with pattern C, agent SDK language support is not a factor; a self-contained single-file binary is possible for the CLI.

*Condition on the decision:* the main risk is non-determinism in workflow code, which the .NET SDK does not catch at runtime the way the Python sandbox does. Replay tests in CI (S8) are mandatory from the first workflow, and workflow code gets a review rule: no `Task.Run`, no wall clock, no unordered collections, no I/O.

*Argument removed (review 01, F1):* the enterprise adopter's Windows-only .NET Framework repositories are **not** an argument for a .NET engine. The engine does not build target repositories; sandboxes do. Those repositories need a Windows sandbox, which S1 already covers. Do not reintroduce this argument.

### DP3. Does the interpreter need a step-level child workflow?
Options: (a) every step is an activity; (b) steps with their own durable lifecycle are child workflows.
*Recommendation:* **(b), but narrowly.** Only `run.child`, `delivery.deploy` and possibly `human.session` need it. Everything else is an activity. Child workflows add history and operational cost; do not use them by default.

### DP4. Where does the findings ledger live?
Options: (a) record store only; (b) record store plus a capped summary in workflow state; (c) workflow state only.
*Recommendation:* **(b).** The merge bar (D8) is evaluated by the workflow, so it needs the summary in state. The full bodies belong in the record store. Cap the summary and spill to a reference if exceeded (see DP5).

### DP5. What happens when the findings summary exceeds its cap?
Options: (a) fail the run; (b) keep only blocking findings in state and reference the rest; (c) compact by scope.
*Recommendation:* **(b).** Blocking findings are what the merge bar needs. Non-blocking findings are queried from the record store when needed.

### DP6. Configuration precedence: strict or with safety floors?
Options: (a) most specific layer always wins; (b) most specific wins except a defined set of floors that no lower layer may relax.
*Recommendation:* **(b).** The prototype's "settings conflicted" failure was exactly this. Floors: human approval for default-branch merges, secrets by reference, protected steps, attempt ceilings. Keep the floor list short and explicit.

### DP7. Is a re-plan allowed to change a completed step?
Options: (a) no, only future steps; (b) yes, it can invalidate and re-run a completed step.
*Recommendation:* **(a) for v1.** Invalidation semantics are subtle and the audit story is hard. Allow inserting and amending future steps, and allow adding a new step that supersedes an earlier one. Revisit if real runs need it.

### DP8. Who can steer, and how is that authorised?
*Recommendation:* the API authenticates the human, evaluates the `steering` policy, and writes a steering record before sending the update. The workflow trusts the API. Do not put authorisation logic in the workflow. See Q5.

### DP9. Should the engine support running without Temporal at all?
Options: (a) no, Temporal is required; (b) yes, an in-process mode for solo developers and tests.
*Recommendation:* **(a) for the product, (b) for tests.** A second execution backend doubles the determinism and idempotency work. A solo developer can run `temporal server start-dev` in one container. Keep the interpreter free of Temporal-specific types where cheap, but do not build a second backend.

### DP10. Epic-level review: aggregate diff or per-ticket roll-up?
*Recommendation:* **aggregate diff.** The prototype's cross-ticket findings were only visible across tickets. A roll-up of per-ticket findings would have missed them. Cost: the aggregate diff can be large, so scope it by the repositories and paths the epic touched.

### DP11. Where does the agent loop run? — settled
Options: (a) the agent CLI or SDK runs inside the sandbox and the agent worker starts it and reads its output; (b) the agent loop runs in the agent worker and each tool call is executed remotely in the sandbox; (c) (a) plus the agent runtime's hooks calling back to the engine.

*Decided (product owner, 2026-10-26):* **(c), "agent in sandbox with engine hooks".** See the architecture document, section 1.4. Option (b) is rejected, not deferred: it requires building and maintaining an agent harness (context management, compaction, edit tools, sub-agents, MCP, prompt caching) when vendor harnesses are better and improve monthly; hundreds of tool calls per step as Temporal activities means thousands of history events per step; and it adds network latency on every tool call. Commit-level checkpoints plus session resume are expected to be enough; measure before revisiting.

*Consequence:* the engine starts processes and serves hook callbacks over HTTP, so agent SDK language support is no longer a factor in the language choice (DP2).

---

## Q. Open questions

### Decided (review 01, 2026-10-26)

| # | Question | Decision |
|---|---|---|
| Q1 | Language: .NET or Python (brief Q1) | **.NET.** See DP2, including the condition on replay tests and workflow-code review |
| Q2 | Is the artifact store content-addressed and immutable, or mutable with versions? | **Content-addressed and immutable.** A new version is a new hash plus a named pointer in the record store |
| Q3 | What exactly is in the record store, and what is its query model? | **Relational, queried through the API.** Runs, step runs, findings ledger, decisions, steering log, timeline events, cost per step. Timeline append-only |
| Q4 | How do human decisions and steering reach the workflow: signals or updates (brief Q5)? And how does the API authenticate and authorise them? | **Updates**, carrying gate id and plan revision, with a validator that rejects stale or invalid requests. Reads come from the record store, not Temporal queries. OIDC for humans, service-account tokens for automation, per-step short-lived tokens for sandbox hooks. Roles from the policy model, scoped by team or repository |
| Q5 | Identity: what identity do agents and the engine use against the tracker and code host, per profile (brief Q8)? | **GitHub App** (or host equivalent) for the engine; a token minted per step for agents; service account for the tracker; the developer's own token in the solo profile. Behind a **credential broker** interface |
| Q6 | Provider packaging: in-process packages, out-of-process plug-ins, or both (brief Q7)? | **In-process .NET packages (NuGet) in v1**, with transport-neutral interfaces so a remote provider adapter stays possible later. Organisation customisation through a **distribution repository**, not a fork |
| Q10 | Artifact and audit store: Git, object storage, a database, or a combination (brief Q4)? | **A combination, and Git is not a store.** Artifact store (local filesystem solo; S3 or Azure Blob) plus record store (SQLite solo; Postgres). Publishing artifacts to a repository for human review is an `artifact.publish` step |
| Q13 | What is the retention and archival story for a finished run, given Temporal deletes history? | **The record store is the long-term audit record.** Temporal history is transient and deleted after namespace retention |

Full reasoning for each is in `docs/reviews/review-01.md`.

### Must settle

None outstanding. Q1-Q6 are decided.

### Should settle

| # | Question | Why it blocks |
|---|---|---|
| Q7 | Sandbox definition: devcontainer.json, the engine's own manifest, or both (brief Q3)? | Affects the repository manifest and the sandbox provider |
| Q8 | Docker-dependent tests on Kubernetes: privileged pods, sysbox, remote Docker host, or CI delegation (brief Q2)? | Affects the sandbox provider and the test step |
| Q9 | Windows-only repositories: can a sandbox provider support Windows hosts (brief Q9)? | Affects worker pools and the test step |
| Q11 | Concurrency model for several tickets in one repository: serialised, integration branch, or per-ticket branches with a merge queue? | Affects the source control steps and the delivery design |
| Q12 | How are attempt budgets counted, and can steering exceed the configured ceiling? | Affects the policy model and the steering API |
| Q14 | How does a run resume or retry after a partial delivery across several repositories? | Affects the delivery steps |
| Q16 | Cost controls: per-run token and compute budgets, and what happens when exceeded | Moved up from "Later" (review 01, F6). At minimum, record tokens, model and compute time per step from the first version; budgets can come later |

### Later

| # | Question |
|---|---|
| Q15 | Multi-tenancy: is one installation ever shared across organisations? |
| Q17 | Observability contract: what metrics, traces and logs must every provider emit |
| Q18 | Disaster recovery: what happens to in-flight runs if the record store is lost |
| Q19 | How does the engine handle a repository that changes its manifest mid-run |
| Q20 | Interactive human sessions: chat in the console, or hand off to an external tool |

---

## Suggested order of work

1. ~~Settle Q1 (language).~~ Done: .NET (DP2).
2. ~~Settle Q2, Q3, Q10 together: the storage model.~~ Done.
3. ~~Settle Q4, Q5: the control plane and identity.~~ Done.
4. ~~Settle Q6: provider packaging.~~ Done.
5. Write the provider interfaces and the step catalogue. **Next.**
6. Settle Q7, Q8, Q9: the sandbox and test story.
7. Write the configuration schema and the three profiles.
8. Write the decision records for the brief's Q1 to Q9.
9. Plan the spike: an interpreter workflow that survives a worker crash and applies a mid-run re-plan.
