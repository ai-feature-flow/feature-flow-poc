# Working list: assumptions, suggestions, discussion points, open questions

Status: all questions decided. Written 2026-10-25; decisions from reviews 01 to 04
(2026-10-26). Current-status notes added 2026-10-31 where the spikes or later
design decisions changed an answer.

Companion to `docs/01-architecture-first-pass.md`, which applies these decisions.
Decision records for the nine questions in the brief are in
`docs/03-decision-records.md`.

| Prefix | Meaning |
|---|---|
| **A** | Assumption made to write the first pass |
| **S** | Suggestion: a design choice proposed in the first pass |
| **DP** | Discussion point: a fork in the road, with a recommendation |
| **Q** | Open question at the time; all are now decided |

**Reading the status notes.** This list keeps its original decisions. Where a
later decision changed one, a **Now:** note says what changed and where the
current design is. The main later changes are:

- **Principle** (product owner, 2026-10-31): the engine provides mechanisms and
  guardrails; the workflow decides. Where earlier text fixed a workflow choice in
  the engine (levels, review bases, delivery points), it is now a workflow choice
  the engine supports.
- **Merge design** (`docs/20`, 2026-10-31): no merge lock. Work catches up with
  its parent branch, re-tests if that changed anything, then merges; where the
  engine merges, a merge onto a moved branch is refused and the work catches up
  again.
- **Review design** (`docs/19`, 2026-10-31): the reviewer is an agent on a
  read-only checkout of the branch. It reads the diff and any file itself; no diff
  is pre-loaded into its prompt.
- **Design requirement DR13** (`docs/08` section 1.3): agents never hold a
  credential that can push; their code host credentials are read-only and per
  step. The agent commits locally; the engine detects new commits through the
  VCS provider, pushes them to a hidden work-in-progress reference while the step
  runs, and moves the ticket branch once when the step succeeds.
- **Merge model** (product owner, 2026-10-31): the merge step takes
  `engine_can_merge` (the engine merges, or a human merges on the code host and
  the engine detects it) and `squash`. A typical three-level workflow: ticket
  into epic branch, squash, engine merges; epic into project branch, plain
  merge, engine merges; project branch into `main`, plain merge, human merges by
  default. Where delivery to `main` happens (once at the end, or several times)
  is the workflow's choice.
- **Default-branch merge setting**: floor F1 (a human must approve any merge into
  the default branch) is now a configurable setting with the same safe default:
  the engine may not merge into the default branch. It is set by organisation,
  repository and workflow, can be locked by the organisation, and nothing an agent
  can edit can change it. If the engine is allowed to merge but the code host
  refuses (for example its protection requires an independent review), that is
  an ordinary intervention: a request for a human to act.
- **Levels are defined by the workflow**: at every level, work is reviewed
  against the branch it was taken from (its parent) and merged back into it, up
  to and including `main`. Ticket, epic and project are typical; a workflow may
  use fewer or more. A run at one level starts the runs of the level below
  (`run.child`).

---

## A. Assumptions

| # | Assumption | If wrong |
|---|---|---|
| A1 | A run maps to exactly one Temporal workflow execution; a ticket inside an epic is a child workflow. **Now:** the levels are defined by the workflow (ticket, epic and project are typical); a run at any level starts the runs of the level below as child workflows | Changes the composition model and the epic-level review design |
| A2 | The engine never stores code, diffs or transcripts in workflow state; references only | Changes the artifact store design and history budget |
| A3 | Human waits are unbounded. A run may sit at a gate for days | Changes timeout and reminder design |
| A4 | The engine is the only writer to the branches and PRs it creates. **Now:** strengthened by DR13: agents commit in their sandbox and the engine pushes for them | Changes conflict handling and merge design |
| A5 | One repository per ticket is the common case; multi-repository tickets are supported but rarer | Changes the default plan shape |
| A6 | Agents work on a branch; the engine owns branch creation, PRs and merges. **Now:** the engine also pushes the agent's commits (DR13) | Changes the source control step set |
| A7 | "Deploy" means trigger and gate an external deployment system, not perform it | Adds a whole subsystem |
| A8 | The record store is queryable (SQL-like), not just a blob store | Changes the console and reporting design |
| A9 | Providers are trusted code. No sandboxing of a provider itself | Changes the packaging model (see Q6, provider packaging) |
| A10 | A run is single-tenant: one organisation, one configuration | Changes the API and namespace model |
| A11 | The engine may run several tickets of one epic concurrently. **Now:** shown correct in Spike 3 (`docs/18`) | Changes the integration branch design (see Q11, concurrency model) |
| A12 | Findings are scoped to a repository and a diff, and can be re-scoped to an epic or delivery | Changes the findings ledger schema |
| A13 | The API is the only way humans interact with a run. No direct Temporal access | Changes the authorisation model |
| A14 | Interpreter versioning uses Temporal's patching API; plan schema versioning is separate | Changes the versioning strategy |

---

## S. Suggestions

| # | Suggestion | Why |
|---|---|---|
| S1 | Route agent steps to task queues by capability (`feature-flow-agent:os-windows`, `feature-flow-agent:docker`) | Handles Windows-only repositories and Docker-dependent tiers without special cases in the interpreter |
| S2 | One `applyPatch` function for re-plans and steering, with protected steps enforced there | Single audit and validation path; closes the "re-plan removed a protected step" hole |
| S3 | Capability negotiation at run start, not per step | A run fails in the first minute, not at step 40 |
| S4 | Deterministic idempotency key plus hidden markers on every remote object the engine creates | Retries adopt existing PRs and branches instead of duplicating them |
| S5 | Contract test suite per provider interface, shipped with the engine | A forking organisation can prove its provider conforms |
| S6 | Fingerprint findings on rule, repository, path, symbol and normalised snippet. Never line numbers | Declined findings stay declined until the code actually changes |
| S7 | Full finding bodies in the record store; capped summary in workflow state | Keeps history small without losing the findings ledger |
| S8 | Replay tests in CI: recorded histories replayed against a changed interpreter | The only reliable way to catch non-determinism |
| S9 | Classify `unauthenticated` and `forbidden` as non-retryable after a short budget | Almost always a configuration fault; retrying hides it |
| S10 | Validate configuration at startup and again at run start | Fails fast, with a clear message |
| S11 | Short-lived, least-privilege, per-step credentials for sandboxes. **Now:** narrowed further: no push credential for agents (DR13), and no model credential in the agent's environment (DR1, `docs/08` section 1.3) | Limits the blast radius of a compromised or confused agent |
| S12 | Treat CI delegation (running a test tier in the real CI system) as a first-class path, not a fallback | The prototype's worst problems came from sandbox and CI divergence |
| S13 | Notifications for "run stuck" as well as "decision needed" | The prototype stalled silently |
| S14 | Version the plan schema separately from the interpreter | Workflow content can evolve without interpreter changes |

---

## DP. Discussion points

Status: DP1, DP2 and DP11 were decided by the product owner. Review 01 accepted
DP6, DP9 and DP10. DP3 to DP5, DP7 and DP8 are first-pass recommendations that no
review overturned.

### DP1. Project name — settled

**Feature Flow.** `feature-flow` in code, configuration, paths and package names.
This repository is `feature-flow-poc`: a proof of concept that may evolve into the
product or be rewritten. The repository manifest directory (`.feature-flow/`),
Temporal namespaces and task queues use the same prefix.

### DP2. Language: .NET or Python (brief Q1) — settled

| | .NET | Python |
|---|---|---|
| Temporal SDK | First-class, well maintained | First-class, well maintained |
| Agent integration | Not a factor under pattern C (DP11): the engine starts the agent runtime and serves hook callbacks over HTTP. Where it matters, the GitHub Copilot SDK supports .NET; the Claude Agent SDK is TypeScript and Python only, so .NET would use the Claude API client or run the Claude Code CLI as a subprocess | Full Claude Agent SDK and Copilot SDK support |
| Typing | Strong typing across a large provider surface and for an organisation maintaining a distribution | Weaker compile-time guarantees |
| Contributor appeal | Smaller open-source contributor pool for this domain | Larger AI/ML contributor pool |
| Adopter fit | Supported by the first enterprise adopter, with worker templates available | Not the adopter's platform |
| Packaging | Self-contained single-file binary possible for the CLI | Requires a runtime on the host |

**Decided (product owner, 2026-10-26): .NET**, for the typing, adopter-fit and
packaging reasons above. Agent SDK language support is not a factor under
pattern C.

**Condition.** The main risk is non-determinism in workflow code, which the .NET
SDK does not catch at runtime the way the Python sandbox does. Replay tests in CI
(S8) are mandatory from the first workflow, and workflow code has a review rule:
no `Task.Run`, no wall clock, no unordered collections, no I/O.

**Argument removed** (review 01, finding F1): the enterprise adopter's
Windows-only .NET Framework repositories are **not** an argument for a .NET
engine. The engine does not build target repositories; sandboxes do. Those
repositories need a Windows sandbox, which capability routing (S1) covers. Do not
reintroduce this argument.

### DP3. Does the interpreter need a step-level child workflow?

Options: (a) every step is an activity; (b) steps with their own durable lifecycle
are child workflows.

**Recommendation: (b), but narrowly.** Only `run.child`, `delivery.deploy` and
possibly `human.session` need it. Everything else is an activity. Child workflows
add history and operational cost; do not use them by default.

### DP4. Where does the findings ledger live?

Options: (a) record store only; (b) record store plus a capped summary in workflow
state; (c) workflow state only.

**Recommendation: (b).** The workflow evaluates the merge bar (decision D8 in
`brief.md`: no unresolved blocking finding, and every finding fixed or declined
with a recorded reason), so it needs the summary in state. The
full bodies belong in the record store. Cap the summary and spill to a reference
if exceeded (DP5).

**Now:** the ledger is also the source of truth over any single review pass, and
"fixed" is sticky while the code is unchanged (`docs/17` section 6.5, positions
L1 and L2).

### DP5. What happens when the findings summary exceeds its cap?

Options: (a) fail the run; (b) keep only blocking findings in state and reference
the rest; (c) compact by scope.

**Recommendation: (b).** Blocking findings are what the merge bar needs.
Non-blocking findings are read from the record store when needed.

### DP6. Configuration precedence: strict, or with safety floors? — accepted

Options: (a) the most specific layer always wins; (b) the most specific layer wins
except for a defined set of floors that no lower layer may relax.

**Accepted (review 01): (b).** The prototype's "settings conflicted" failure was
exactly this. Floors: human approval for default-branch merges, secrets by
reference, protected steps, attempt ceilings. Keep the list short and explicit.
The floors are F1 to F4 in `docs/07` section 2. **Now:** F1 is a configurable
setting with the same safe default (the engine may not merge into the default
branch), set by organisation, repository and workflow; the organisation can lock
it, and nothing an agent can edit can change it. Within that cap, whether the
engine merges into `main` is the workflow's choice.

### DP7. May a re-plan change a completed step?

Options: (a) no, only future steps; (b) yes, it can invalidate and re-run a
completed step.

**Recommendation: (a) for version 1.** Invalidation semantics are subtle and the
audit story is hard. Allow inserting and amending future steps, and adding a new
step that supersedes an earlier one. Revisit if real runs need it.

### DP8. Who can steer, and how is that authorised?

**Recommendation:** the API authenticates the human, evaluates the `steering`
policy, and writes a steering record before sending the update. The workflow
trusts the API; no authorisation logic in the workflow. See Q4.

### DP9. Should the engine run without Temporal at all? — accepted

Options: (a) no, Temporal is required; (b) yes, an in-process mode for solo
developers and tests.

**Accepted (review 01): (a) for the product, (b) for tests.** A second execution
backend doubles the determinism and idempotency work. A solo developer can run
`temporal server start-dev` in one container. Keep the interpreter free of
Temporal-specific types where cheap, but do not build a second backend.

### DP10. Epic-level review: aggregate diff or per-ticket roll-up? — accepted

**Accepted (review 01): review the aggregate change.** The prototype's
cross-ticket findings were only visible across tickets; a roll-up of per-ticket
findings would have missed them. Spike 3 confirmed it: the epic review caught a
seeded cross-ticket defect that both ticket reviews missed (`docs/18` section 3).
Cost: the aggregate change can be large, so the review is scoped by the
repositories and paths the epic touched.

**Now:** generalised to every level, and the reviewer reads the change from a
checkout rather than being given the diff (review design, `docs/19`). At any
level the workflow defines, the work is reviewed against the branch it was taken
from and will merge back into: a ticket against its epic branch, an epic against
its project branch, a project against `main`. Each level looks for problems that
only exist once its children are combined. The reviewer is an agent on a
read-only checkout and reads the diff and any file itself.

### DP11. Where does the agent loop run? — settled

Options: (a) the agent CLI or SDK runs inside the sandbox, and the agent worker
starts it and reads its output; (b) the agent loop runs in the agent worker and
each tool call runs remotely in the sandbox; (c) (a) plus the agent runtime's
hooks calling back to the engine.

**Decided (product owner, 2026-10-26): (c), "agent in sandbox with engine hooks"**
(pattern C, `docs/01` section 1.4). Option (b) is rejected, not deferred:

- it needs an agent harness (context management, compaction, edit tools,
  sub-agents, MCP, prompt caching) when vendor harnesses are better and improve
  monthly;
- hundreds of tool calls per step as Temporal activities means thousands of
  history events per step;
- it adds network latency to every tool call.

Commit-level checkpoints plus session resume are expected to be enough; measure
before revisiting.

**Consequence:** the engine starts processes and serves hook callbacks over HTTP,
so agent SDK language support is not a factor in the language choice (DP2).

**Now:** Claude Code's hooks fired on Windows and macOS in Spike 2; Copilot CLI's
before- and after-tool-call hooks did not fire with a third-party model
(`docs/16` section 4).

---

## Q. Open questions

All decided. Full reasoning is in `docs/reviews/review-01.md`, `review-03.md` and
`review-04.md`.

### Decided (review 01, 2026-10-26)

| # | Question | Decision |
|---|---|---|
| Q1 | Language: .NET or Python (brief Q1) | **.NET.** See DP2, including the condition on replay tests and the workflow-code review rule |
| Q2 | Is the artifact store content-addressed and immutable, or mutable with versions? | **Content-addressed and immutable.** A new version is a new hash plus a named pointer in the record store |
| Q3 | What is in the record store, and what is its query model? | **Relational, queried through the API.** Runs, step runs, findings ledger, decisions, steering log, timeline events, cost per step. Timeline append-only |
| Q4 | How do human decisions and steering reach the workflow: signals or updates (brief Q5)? How does the API authenticate and authorise them? | **Updates**, carrying gate id and plan revision, with a validator that rejects stale or invalid requests. Reads come from the record store, not Temporal queries. OIDC for humans, service-account tokens for automation, per-step short-lived tokens for sandbox hooks. Roles from the policy model, scoped by team or repository |
| Q5 | What identity do agents and the engine use against the tracker and code host, per profile (brief Q8)? | **GitHub App** (or host equivalent) for the engine; a token minted per step for agents; a service account for the tracker; the developer's own token in the solo profile. All behind a **credential broker** interface. **Now:** an agent's code host token is read-only and per step, scoped to the run's repository plus any repositories the workflow declares it may read; it never carries push access. The engine pushes the agent's commits and runs every merge step (DR13, `docs/20`) |
| Q6 | Provider packaging: in-process packages, out-of-process plug-ins, or both (brief Q7)? | **In-process .NET packages (NuGet) in version 1**, with transport-neutral interfaces so a remote provider adapter stays possible. Organisation customisation through a **distribution repository**, not a fork |
| Q10 | Artifact and audit store: Git, object storage, a database, or a combination (brief Q4)? | **A combination; Git is not a store.** Artifact store (local filesystem solo; S3 or Azure Blob) plus record store (SQLite solo; Postgres). Publishing artifacts to a repository for human review is an `artifact.publish` step |
| Q13 | Retention and archival of a finished run, given Temporal deletes history? | **The record store is the long-term audit record.** Temporal history is transient and deleted after namespace retention |

### Decided (review 03, 2026-10-26)

| # | Question | Decision |
|---|---|---|
| Q7 | Sandbox definition: `devcontainer.json`, the engine's own manifest, or both (brief Q3)? | **Both; the manifest is root `feature-flow.yaml`** and is authoritative. `devcontainer.json` is an optional input. `.feature-flow/` is reserved but unused in version 1 |
| Q8 | Docker-dependent tests on Kubernetes: privileged pods, sysbox, remote Docker host, or CI delegation (brief Q2)? | **CI delegation by default**, sysbox where the platform supports it, privileged pods in the solo profile only. Per tier, declared in `feature-flow.yaml`. **Now:** CI delegation shown working in Spike 2 (`docs/16` section 2) |
| Q9 | Can a sandbox provider support Windows hosts (brief Q9)? | **Not in version 1.** Version 1 has the hooks for it: `Os[linux, windows]` capability, `os: windows` in the manifest, `os-windows` queue routing, CI delegation. Spike W0 (agent runtimes and hooks on Windows) comes before a Windows VM sandbox. **Now:** W0 partly answered (`docs/08` section 2.4) |
| Q11 | Concurrency model for several tickets in one repository | **Per-ticket branches plus `scm.merge_base_forward` plus a serialised merge lock.** One epic integration branch per repository. `max_concurrency` on the `parallel` step confirmed. Optional `MergeQueue` capability. **Now: the merge lock is superseded** by the merge design (`docs/20`): no lock; catch up, re-test if changed, then merge. Where the engine merges, a merge onto a moved branch is refused and returns `base_moved`, and the work catches up again, up to the merge step's attempt allowance (default 3). The same flow applies at every level the workflow defines (a ticket into its epic branch, an epic into its project branch). Per-ticket branches, the integration branch, `scm.merge_base_forward`, `max_concurrency` and the optional `MergeQueue` capability stay; GitHub's merge queue is an example of that capability |
| Q12 | How are attempt budgets counted, and can steering exceed the configured ceiling? | **One counting rule:** every entry into a step counts. Infrastructure failures are free. Resumes are free only with progress (a new commit, or session growth); after 3 consecutive resumes with no progress the next counts as a restart. Steering grants extra attempts up to a hard ceiling. **Now:** ceilings are enforced on every execution, including loop re-entry (DR10, `docs/08` section 1.3). Like every limit, they are configured hierarchically: engine default, then workflow, child workflow and step |
| Q14 | How does a run resume or retry after a partial delivery across several repositories? | **One idempotent child workflow per repository plus a delivery ledger in the record store.** No compensating action; A7 (deploy means trigger an external system) holds. On failure: retry, fix forward, or revert |
| Q16 | Cost controls: per-run token and compute budgets, and what happens when exceeded? | **Policy-driven, not in version 1.** Version 1 records tokens, model and compute time per step. When budgets arrive, exceeding one stops at a gate, never a hard stop |

### Decided (review 04, 2026-10-26)

| # | Question | Decision |
|---|---|---|
| Q15 | Is one installation ever shared across organisations? | **One organisation per installation** (A10 holds). Teams scoped by the policy model. `org_id` in record store keys from version 1; no other multi-tenant features |
| Q17 | What metrics, traces and logs must every provider emit? | **OpenTelemetry**, OTLP exporter. One trace per run; spans per step, activity and provider call. The engine decorates every provider call, so providers only add attributes |
| Q18 | What happens to in-flight runs if the record store is lost? | **No custom disaster recovery.** Database backups with point-in-time restore; artifact store versioning and replication; a reconcile job rebuilds the run index from Temporal |
| Q19 | How does the engine handle a repository that changes its manifest mid-run? | **Each step reads `feature-flow.yaml` from its branch when it starts** and records the hash of the resolved manifest. Nothing is copied into the plan. Capability negotiation re-runs if a needed capability disappears. **Now:** shown working in Spike 2 (`docs/16` section 2). One exception: the repository's default-branch merge setting (floor F1) is read from the default branch, never the ticket branch, so an agent cannot change it |
| Q20 | Interactive human sessions: chat in the console, or hand off to an external tool? | **Hand-off to the human's own agent tool (surface A) is the version 1 MVP.** The session is an engine mechanism; the surface is pluggable. Pre-submit checks run the same validators as the gated steps. **Now:** for review, parity means same brief, same tools, same model, same commit (`docs/19`). A "merge when ready" tool may only ask the engine to run the merge step (`docs/20`) |

### Open

None.

---

## Order of work

The first-pass order of work is complete:

| Item | Where it landed |
|---|---|
| Settle Q1 (language) | DP2 |
| Settle storage (Q2, Q3, Q10), control plane and identity (Q4, Q5), packaging (Q6) | Decided above |
| Provider interfaces and step catalogue | `docs/04`, `docs/05`, `docs/06` |
| Sandbox and test story (Q7, Q8, Q9) | Decided above |
| Configuration schema and the three profiles | `docs/07` |
| Decision records for the brief's Q1 to Q9 | `docs/03` |
| Spike plan | `docs/08`; the spikes ran and are closed (`docs/15`, `docs/16`, `docs/18`) |
