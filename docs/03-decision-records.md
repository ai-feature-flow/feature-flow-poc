# Decision records

Written 2026-10-26; current-status notes added 2026-10-31. Deliverable 5 of
`brief.md` section 11: one decision record for each of the nine open questions in
`brief.md` section 10.

Each record gives the options, the decision, and where it is applied. The
decisions come from `docs/reviews/review-01.md`, `review-03.md` and
`review-04.md`; all were made by the product owner on 2026-10-26. The records keep
the original decision. Where the spikes or a later decision changed it, a
**Current status** line says what changed and where.

The `docs/02` IDs used below (A, S, DP, Q) are explained where they appear.
Risk numbers (R1 to R20) are in `docs/08` section 1; design requirements (DR1 to
DR13) are in `docs/08` section 1.3.

| Brief # | Question | Decision | Current status (2026-10-31) |
|---|---|---|---|
| Q1 | .NET or Python | .NET, conditional on replay tests and a workflow-code review rule | Stands |
| Q2 | Docker-dependent tests on Kubernetes | CI delegation by default; sysbox where supported; privileged pods solo only | Stands; CI delegation shown in Spike 2 |
| Q3 | Sandbox definition | Both: root `feature-flow.yaml` authoritative, `devcontainer.json` an optional input | Stands |
| Q4 | Artifact and audit store | Artifact store plus record store. Git is not a store | Stands |
| Q5 | Decisions and steering transport, API auth | Temporal Updates; OIDC, service-account tokens, per-step hook tokens | Stands; shown in Spikes 1 and 2 |
| Q6 | Plan-as-data within Temporal limits | Closed by architecture section 3.5 plus the storage decision, conditional on measuring history size | **Closed by Spike 1**, with a change to the threshold |
| Q7 | Provider packaging | In-process NuGet in version 1; transport-neutral interfaces; distribution repository | Stands |
| Q8 | Identity | GitHub App, per-step minted tokens, behind a credential broker | Stands, **narrowed**: agents hold no push credential (DR13) and no model credential (DR1) |
| Q9 | Windows-only repositories | Not in version 1; hooks in version 1; spike before a Windows sandbox | Stands; Spike W0 partly answered |

---

## Q1. .NET or Python

**Options.** (a) .NET. (b) Python.

**Decision: (a) .NET.**

Reasons:

- strong typing across a large provider surface, and for an organisation
  maintaining a distribution;
- supported by the first enterprise adopter, with worker templates available;
- with pattern C (the agent runtime runs inside the sandbox and calls the engine
  through hooks, `docs/01` section 1.4), agent SDK language support is not a
  factor;
- a self-contained single-file binary is possible for the CLI.

**Condition.** The main risk is non-determinism in workflow code, which the .NET
SDK does not catch at runtime the way the Python sandbox does. Replay tests in CI
are mandatory from the first workflow (`docs/02` S8), and workflow code has a
review rule: no `Task.Run`, no wall clock, no unordered collections, no I/O.
Recorded as risk R12 (non-determinism in .NET workflow code).

**Argument rejected.** The enterprise adopter's Windows-only .NET Framework
repositories are not an argument for a .NET engine. The engine does not build
target repositories; sandboxes do. Those repositories need a Windows sandbox,
which routing agent steps by capability covers (`docs/02` S1). See review 01,
finding F1.

**Current status.** Stands. Replay tests caught an unversioned interpreter change
in Spike 3 (`docs/18` section 4).

**Applied:** `docs/02` DP2 (the language discussion).

---

## Q2. Running Docker-dependent tests from sandboxes on Kubernetes

**Options.** (a) Privileged pod, Docker-in-Docker. (b) sysbox or another rootless
runtime. (c) Remote Docker host or socket proxy. (d) Delegate the tier to CI.

**Decision: (d) as the default for Docker-dependent tiers, (b) where the platform
supports it, (a) only in the solo profile.**

Reasons:

- "Sandbox does not match CI" (risk R5) was one of the prototype's worst
  problems: a test that deadlocked only on CI runners is precisely the failure
  delegation avoids.
- Delegation is the only option that guarantees the same environment, and treats
  CI delegation as a first-class path, not a fallback (`docs/02` S12).
- It avoids privileged pods in the enterprise profile, where a node escape
  matters most.

**Scope.** Per tier, declared in `feature-flow.yaml`. A repository with no
Docker-dependent tests pays nothing.

**Current status.** Stands. Spike 2 ran a Docker-dependent tier through CI
delegation and it passed (`docs/16` section 2).

**Applied:** `docs/01` section 5.1.1 (`feature-flow.yaml` and execution targets).

---

## Q3. Sandbox definition

**Options.** (a) `devcontainer.json` only. (b) The engine's own manifest only.
(c) Both, with the manifest authoritative.

**Decision: (c), with the file renamed and moved to root `feature-flow.yaml`.**

`devcontainer.json` describes a developer environment, not a build and test
environment: it has no test tiers, no CI mapping, no network policy, no secret
references, and many repositories do not have one. The engine's file stays
authoritative because it carries what `devcontainer.json` cannot express.

Consequences:

- The environment is defined inline or referenced from an existing
  `devcontainer.json`. Repositories without a devcontainer need only the one file.
- Other files (Dockerfiles, compose files) are referenced by relative path.
- `.feature-flow/` is reserved for possible later use. Not used in version 1.
- Terms: `feature-flow.yaml` (as written); **resolved manifest** (after merging
  the referenced devcontainer and defaults; produced by the engine, so providers
  never parse either source file); **execution target** (sandbox, local or CI).

**Current status.** Stands. Spike 2 showed a mid-run edit to `feature-flow.yaml`
picked up by the next step (`docs/16` section 2).

**Applied:** `docs/01` sections 5.1.1 (`feature-flow.yaml` and execution targets)
and 5.1.2 (the manifest is read per step).

---

## Q4. Artifact and audit store

**Options.** (a) Git. (b) Object storage. (c) A database. (d) A combination.

**Decision: (d), and Git is not a store.**

| Store | Contents | Technology by profile | Model |
|---|---|---|---|
| Temporal history | Working state only: references and small summaries | Temporal | Transient; deleted after namespace retention |
| Artifact store | Specs, plans and every plan revision, diffs, transcripts, logs, reports | Local filesystem (solo); S3 or Azure Blob | Immutable, content-addressed. A new version is a new hash plus a named pointer in the record store |
| Record store | Runs, step runs, findings ledger, decisions, steering log, timeline events, cost per step | SQLite (solo); Postgres | Relational, queried through the API. Timeline append-only. The long-term audit record |

Publishing artifacts to a repository for human review is an engine step
(`artifact.publish`). Nobody needs to browse run state through Git; the console
and API replace it.

**Current status.** Stands.

**Applied:** `docs/01` section 2.1 (storage model).

---

## Q5. How human decisions and steering reach the workflow, and API authorisation

**Options.** (a) Temporal Signals. (b) Temporal Updates.

**Decision: (b) Updates.**

- Each update carries the gate id and plan revision it applies to; the update
  validator rejects stale or invalid requests (no open gate, plan revision
  changed). The API retries against the current run after continue-as-new
  (Temporal restarting the workflow with fresh history), which mitigates risk R3
  (a decision lost during continue-as-new).
- Reads do not use Temporal queries. The console and API read from the record
  store.

**Authentication:**

- humans: OIDC against the organisation's identity provider (a local token in the
  solo profile);
- automation: service-account API tokens;
- agent hooks from the sandbox: a short-lived token issued per step, scoped to
  that run and step's hook endpoint.

**Authorisation:** roles from the policy model (viewer, approver, steerer, admin),
mapped to identity-provider groups, scoped by team or repository. Only the API
authorises and writes the audit record; the workflow trusts the API (`docs/02`
DP8). Only the API and workers can reach the Temporal namespace.

**Current status.** Stands. Spike 1 showed no update lost during continue-as-new
(`docs/15` section 3.6); Spike 2 approved a gate from chat through an update
(`docs/16` section 2).

**Applied:** `docs/01` section 5.4 (control plane).

---

## Q6. Plan-as-data within Temporal limits

**Question.** History size, continue-as-new, and how re-plans are versioned and
audited.

**Decision: closed by `docs/01` section 3.5 (Temporal constraints) plus the
storage decision (Q4), on the condition below.**

- **History size.** Workflow state holds references and small summaries only. The
  findings summary is capped (`docs/02` DP5). Continue-as-new happens at a
  measured threshold.
- **Continue-as-new.** Only at a safe point: between steps, with no pending
  decision. The API retries when it finds no open gate (risk R3).
- **Re-plan versioning and audit.** Every plan revision goes to the artifact
  store; the current revision lives in workflow state. Interpreter versioning uses
  Temporal's patching API (`docs/02` A14); the plan schema is versioned separately
  (`docs/02` S14). Re-plans and steering share one `applyPatch` path (`docs/02`
  S2), the single audit and validation point.

**Condition.** The spike must measure history size on a long run with several
re-plans and confirm the continue-as-new threshold.

**Current status: closed by Spike 1, with a change** (`docs/15` sections 6.1 and
9.1). Plan-as-data fits: 6.28 history events per step, continue-as-new triggered
cleanly and replayed correctly. Two changes to the record:

1. **Carried state is compacted.** The continue-as-new input is one payload with a
   2 MB ceiling, so it carries only the plan, cursors and what expressions can
   read; step-run detail goes to the record store. This holds it at about 500
   bytes per plan step.
2. **The threshold is events or bytes, whichever comes first.** Starting value
   5,120 events (half Temporal's 10,240-event warning). For large plans the 2 MB
   payload limit binds first, so a plan size limit keeps carried state under 1 MB,
   about 2,000 steps (`docs/12` section 2.4, check 32).

**Applied:** `docs/01` sections 3.4 (re-planning and steering), 3.5 (Temporal
constraints) and 2.1 (storage model).

---

## Q7. Provider packaging

**Options.** (a) In-process packages. (b) Out-of-process plug-ins. (c) Both.

**Decision: (a) in version 1, with interfaces designed so (c) stays possible.**

- Version 1: providers are in-process .NET packages (NuGet). A host project
  references the providers it ships; configuration selects them by name.
- Provider interfaces are transport-neutral: async, plain serialisable data
  objects, no third-party SDK types in signatures, cancellation and classified
  errors on every call. This keeps a generic remote provider adapter possible
  later, for providers in other languages or needing isolation. The adapter is not
  built in version 1.
- Organisation customisation uses a **distribution repository**, not a fork: a
  separate repository with the organisation's own providers, a small host project
  that builds its worker and API images, and its configuration. A fork is needed
  only for core changes, which should be rare.

**Current status.** Stands. Spike 2 swapped fake providers for real ones without
touching the workflow (`docs/16` section 11).

**Applied:** `docs/01` section 4.5 (provider packaging).

---

## Q8. Identity

**Options.** (a) The developer's or requester's own identity. (b) A bot or service
account. (c) A GitHub App or host equivalent.

**Decision: (c) for the engine against the code host, with per-step minted tokens
for agents, all behind a credential broker.**

| Actor | Identity |
|---|---|
| Engine against the code host | A GitHub App, or the equivalent bot identity on other hosts |
| Agents in the sandbox | A token minted per step from the app identity: one repository, minimum permissions (contents write), about one hour; refreshable through the hook endpoint |
| Engine against the tracker | A service account; every action records "on behalf of" the run's requester or approver |
| Agents against the tracker | None. Tracker writes only through engine steps |
| Solo profile | The developer's own token for both engine and agents |

The **credential broker** is an interface: given an actor, a target system and a
scope, it returns a short-lived credential. GitHub App tokens, service accounts,
personal tokens and future mechanisms (for example OIDC workload identity) are
implementations. No provider reads credentials directly from configuration.

Merges into a default branch: the app cannot approve its own PR. Where branch
protection requires a code host review, the human approves on the code host and
the engine waits for it; otherwise the human approves in the console or chat and
the app merges. The audit record shows the bot as actor and the human as
approver.

**Current status: stands, narrowed by later requirements** (`docs/08`
section 1.3):

- **Agents never hold a credential that can push** (DR13, from the merge design
  `docs/20`). The "contents write" agent token above is superseded: an agent
  commits in its sandbox, and the engine pushes the ticket branch and performs
  every merge. GitHub cannot limit a token to particular branches, so withholding
  the credential is the only reliable control.
- **Model credentials never enter the agent's environment** (DR1): a proxy that
  adds the key, or per-run keys with a limit and expiry.
- **Floor F1** (a human must approve any merge into the default branch) **is
  enforced by the engine at merge time** and backed by code host protection
  (DR2). That protection is configured by an identity the engine does not hold
  (DR3).
- Spike 2 showed broker tokens lasting 60 minutes, covering one repository, with
  wider requests refused (`docs/16` section 2).

**Applied:** `docs/01` section 4.6 (credential broker).

---

## Q9. Windows-only repositories

**Options.** (a) Windows containers. (b) Windows VM or hosted Windows host.
(c) CI delegation for Windows tiers only. (d) Not in version 1.

**Decision: (d) for version 1, with the hooks for (b) and (c) in version 1.**

Version 1 includes:

- the `Os[linux, windows]` sandbox capability; `feature-flow.yaml` can declare
  `os: windows`;
- routing to `feature-flow-agent:os-windows` queues (`docs/02` S1);
- a run that needs Windows and has no provider fails at run-start capability
  negotiation (`docs/02` S3) with a clear message;
- CI delegation of test tiers for Windows repositories.

Roadmap:

| Phase | Approach | Notes |
|---|---|---|
| W0 | Spike: agent runtimes and hooks on Windows | Decides whether pattern C holds on Windows |
| W1 | Windows VM sandbox provider: ephemeral or pooled cloud VMs from an image, agent worker on the VM | Primary approach. CI delegation as fallback for tiers the host cannot run |
| W2 | Windows containers on Windows node pools | Only if density or isolation needs it and the toolchain fits |

Research before W1: image size and start time, and licensing of Windows Server
and build tools in VMs and containers.

**Why the spike comes first.** Under pattern C the agent runtime runs inside the
sandbox, so a Windows sandbox needs a Windows runtime with hook support. This is
the one place pattern C could fail on the adopter's actual repositories. Recorded
as risk R14.

**Current status.** Stands. W0 is partly answered (`docs/08` section 2.4,
from `docs/16` section 4): Claude Code's hooks fired on Windows; Copilot CLI's
before- and after-tool-call hooks did not fire with a third-party model. Session
resume on Windows and the W1 research are not done.

**Applied:** `docs/01` section 4.7 (Windows support).
