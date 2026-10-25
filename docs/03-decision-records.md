# Decision records

Date: 2026-10-26. Deliverable 5 of `brief.md` section 11: one decision record per
open question, with options and a recommendation.

Covers the nine questions in `brief.md` section 10. Each record states the
options, the decision, who decided and when, and where it is applied. The
decisions come from `docs/reviews/review-01.md`, `review-03.md` and `review-04.md`.

| Brief # | Question | Decision | Decided |
|---|---|---|---|
| Q1 | .NET or Python | .NET, conditional on replay tests and a workflow-code review rule | Product owner, 2026-10-26 |
| Q2 | Docker-dependent tests on Kubernetes | CI delegation by default; sysbox where supported; privileged pods solo only | Product owner, 2026-10-26 |
| Q3 | Sandbox definition | Both: root `feature-flow.yaml` authoritative, `devcontainer.json` an optional input | Product owner, 2026-10-26 |
| Q4 | Artifact and audit store | Artifact store plus record store. Git is not a store | Product owner, 2026-10-26 |
| Q5 | Decisions and steering transport, API auth | Temporal Updates; OIDC, service-account tokens, per-step hook tokens | Product owner, 2026-10-26 |
| Q6 | Plan-as-data within Temporal limits | Closed by section 3.5 plus the storage decision, conditional on the spike measuring history size | Product owner, 2026-10-26 |
| Q7 | Provider packaging | In-process NuGet in v1; transport-neutral interfaces; distribution repository | Product owner, 2026-10-26 |
| Q8 | Identity | GitHub App, per-step minted tokens, behind a credential broker | Product owner, 2026-10-26 |
| Q9 | Windows-only repositories | Not in v1; hooks in v1; spike before committing to a Windows sandbox | Product owner, 2026-10-26 |

---

## Q1. .NET or Python

**Options.** (a) .NET. (b) Python.

**Decision: (a) .NET.**

Reasons: strong typing across a large provider surface and for an organisation
maintaining a distribution; supported by the first enterprise adopter, with worker
templates available; with pattern C (section 1.4) agent SDK language support is
not a factor; a self-contained single-file binary is possible for the CLI.

**Condition.** The main risk is non-determinism in workflow code, which the .NET
SDK does not catch at runtime the way the Python sandbox does. Replay tests in CI
(S8) are mandatory from the first workflow, and workflow code gets a review rule:
no `Task.Run`, no wall clock, no unordered collections, no I/O. Recorded as risk
R12.

**Argument explicitly rejected.** The enterprise adopter's Windows-only .NET
Framework repositories are not an argument for a .NET engine. The engine does not
build target repositories; sandboxes do. Those repositories need a Windows
sandbox, which S1 already covers. See review 01, F1.

**Applied:** `docs/02-working-questions.md` DP2.

---

## Q2. Running Docker-dependent tests from sandboxes on Kubernetes

**Options.** (a) Privileged pod, Docker-in-Docker. (b) sysbox or another rootless
runtime. (c) Remote Docker host or socket proxy. (d) Delegate the tier to CI.

**Decision: (d) as the default for Docker-dependent tiers, (b) where the platform
supports it, (a) only in the solo profile.**

Reasons: R5, "sandbox does not match CI", was one of the prototype's worst
problems — a test that deadlocked only on CI runners is precisely the failure
delegation avoids. Delegation is the only option that guarantees the same
environment. S12 already says to treat CI delegation as a first-class path, not a
fallback. It also avoids privileged pods in the enterprise profile, where a node
escape matters most.

**Scope.** Per tier, declared in `feature-flow.yaml`. A repository with no
Docker-dependent tests pays nothing.

**Applied:** architecture section 5.1.1 (execution targets).

---

## Q3. Sandbox definition

**Options.** (a) `devcontainer.json` only. (b) The engine's own manifest only.
(c) Both, with the manifest authoritative.

**Decision: (c), with the file renamed and moved to root `feature-flow.yaml`.**

`devcontainer.json` describes a developer environment, not a build and test
environment: it has no test tiers, no CI mapping, no network policy, no secret
references, and many repositories do not have one. The engine's file must stay
authoritative because it carries what `devcontainer.json` cannot express.

Consequences:

- The environment is defined inline or referenced from an existing
  `devcontainer.json`. Repositories without a devcontainer need only the one file.
- Other files (Dockerfiles, compose files) are referenced by relative path.
- `.feature-flow/` is reserved for possible later use. Not used in version 1.
- Terminology: `feature-flow.yaml` (as written), **resolved manifest** (after
  merging the referenced devcontainer and defaults; produced by the engine, and
  providers never parse either source file), **execution target** (sandbox, local
  or CI).

**Applied:** architecture sections 5.1.1 and 5.1.2.

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
(`artifact.publish`). There is no requirement to browse run state through Git; the
console and API replace it.

**Applied:** architecture section 2.1.

---

## Q5. How human decisions and steering reach the workflow, and API authorisation

**Options.** (a) Temporal Signals. (b) Temporal Updates.

**Decision: (b) Updates.**

- Each update carries the gate id and plan revision it applies to; the update
  validator rejects stale or invalid requests (no open gate, plan revision
  changed). The API retries against the current run after continue-as-new, which
  mitigates R3.
- Reads do not use Temporal queries. The console and API read from the record
  store.

Authentication: OIDC against the organisation's identity provider for humans (a
local token in the solo profile); service-account API tokens for automation; a
short-lived token issued per step for agent hooks from the sandbox, scoped to that
run and step's hook endpoint.

Authorisation: roles from the policy model (viewer, approver, steerer, admin),
mapped to identity-provider groups, scoped by team or repository. Only the API
authorises and writes the audit record; the workflow trusts the API (DP8). Only
the API and workers can reach the Temporal namespace.

**Applied:** architecture section 5.4.

---

## Q6. Plan-as-data within Temporal limits

**Question.** History size, continue-as-new, and how re-plans are versioned and
audited.

**Decision: closed by architecture section 3.5 plus the storage decision, on the
condition below.**

The three parts:

- **History size.** Workflow state holds references and small summaries only. The
  findings summary is capped (DP5). Continue-as-new happens at a measured
  threshold, between steps and never mid-step.
- **Continue-as-new.** Only at a safe point: between steps, with no pending
  decision. The API retries when it finds no open gate (R3).
- **Re-plan versioning and audit.** Every plan revision goes to the artifact
  store; the current revision lives in workflow state. Interpreter versioning uses
  Temporal's patching API (A14); the plan schema is versioned separately (S14).
  Re-plans and steering share one `applyPatch` path (S2), which is the single
  audit and validation point.

**Condition.** The spike must **measure history size** on a long run with several
re-plans and confirm the continue-as-new threshold. Until that measurement exists,
this record is provisional.

**Applied:** architecture sections 3.4, 3.5 and 2.1.

---

## Q7. Provider packaging

**Options.** (a) In-process packages. (b) Out-of-process plug-ins. (c) Both.

**Decision: (a) in version 1, with interfaces designed so (c) stays possible.**

- Version 1: providers are in-process .NET packages (NuGet). A host project
  references the providers it ships; configuration selects them by name.
- Provider interfaces must be transport-neutral: async, plain serialisable data
  objects, no third-party SDK types in signatures, cancellation and classified
  errors on every call. This keeps a generic **remote provider** adapter possible
  later, for providers in other languages or needing isolation. Do not build the
  adapter in version 1.
- Organisation customisation uses a **distribution repository**, not a fork: a
  separate repository with the organisation's own providers and a small host
  project that builds its worker and API images, plus its configuration. A fork is
  needed only for core changes, which should be rare.

**Applied:** architecture section 4.5.

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

**Applied:** architecture section 4.6.

---

## Q9. Windows-only repositories

**Options.** (a) Windows containers. (b) Windows VM or hosted Windows host.
(c) CI delegation for Windows tiers only. (d) Not in version 1.

**Decision: (d) for version 1, with the hooks for (b) and (c) in version 1.**

Version 1 includes:

- `Os[linux, windows]` sandbox capability; `feature-flow.yaml` can declare
  `os: windows`.
- Routing to `feature-flow-agent:os-windows` queues (S1).
- A run that needs Windows and has no provider fails at run-start capability
  negotiation (S3) with a clear message.
- CI delegation of test tiers works for Windows repositories.

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

**Applied:** architecture section 4.7.
