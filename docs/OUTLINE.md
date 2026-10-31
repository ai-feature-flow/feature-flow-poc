# Outline of the design

Status: draft for review, 2026-10-31. Companion to `PLAN.md` (the rewrite plan)
and `00-principles.md` (principles P1 to P12).

For each document: what it owns, its sections, what it takes from the first
design (`archive/docs/`) and from Feature Factory (FF, `../feature-factory/docs/`),
and what changes. Section 3 maps every FF facility and every review finding to
the document that handles it, so nothing is lost. Section 4 lists the questions
the outline raises.

---

## 1. The documents

### 01 Concepts

Owns the domain model. Every other document uses these terms as defined here.

| Section | Content |
|---|---|
| 1 Runs and levels | A run is one unit of work. Levels are named by the workflow and nest; a run starts child runs. Run status (`running`, `completed`, `failed`, `rejected`, `partial`, `incomplete`, `aborted`) and run position (what it is doing or waiting for, from FF) |
| 2 Plans, templates, steps | One schema for template and plan. Three step forms: agent step, human decision, engine action |
| 3 Outcomes and routes | Declared outcomes with an optional class (RW3, `PLAN.md` section 3). Directives: `end`, `escalate`, `resume` |
| 4 Attempts | One counting rule for every step (fixes FF's two rule sets). Infrastructure failures are free; resumes are free only with progress |
| 5 Artifacts and evidence | Immutable, content-addressed, typed. Evidence types only engine actions produce (P6). Revisions and ratification of an exact revision, inherited by child runs (from FF) |
| 6 Findings and the ledger | A finding, its fingerprint, its states. The ledger as the record; review passes as evidence. Policy on findings is library content (RW4) |
| 7 Decisions | One primitive (P4): type, prompt, choices, actor. Kinds: gate, intervention, question, patch approval, human merge, breakpoint |
| 8 Requirements and traceability | Requirements, acceptance criteria, verification methods, evidence claims, the trace. The engine supplies the mechanism; the shapes are library content (RW7) |
| 9 Repositories and branches | Repositories in a run, the repository manifest, branch levels, parent branch, the hidden work-in-progress reference |

Sources: FF `concepts.md`, `artifacts.md`, `verification.md`; first design
`docs/01` sections 2 and 7.2, `docs/09` sections 2 and 4.

### 02 Engine architecture

Owns the components and how they run. Mostly carried over from the first
design, which the spikes evidenced.

| Section | Content |
|---|---|
| 1 Components | API service, orchestration worker, agent worker, sandbox, artifact store, record store, Temporal, console and CLI |
| 2 Task queues and capability routing | Queues per sandbox capability (for example Windows, Docker) |
| 3 The interpreter | The loop; the determinism boundary; dispatch on first completion; parallel branches and joins; how a run ends; interpreter versioning |
| 4 Continue-as-new | Measured threshold; **redesigned safe point** so a wide epic with long children and open decisions can still continue-as-new (review section 6, item 1) |
| 5 Agent steps in sandboxes | Agent runtime inside the sandbox with hooks to the engine; resume after a crash; commits detected and pushed by the engine to a hidden reference; where the hook endpoint is served (open question 6) |
| 6 Engine-side version control | Where the engine's working clones live for catch-up and merge (review section 6, item 8) |
| 7 Control plane | Temporal updates for decisions and overrides; authentication; actor from identity; chat as a client |
| 8 Stores | Artifact store, record store, what lives in Temporal state; tenancy; disaster recovery |
| 9 Observability and cost | OpenTelemetry; cost recorded per step |
| 10 Data flows | One ticket run; an epic with a child's re-plan signal; a project delivering to the default branch |

Sources: first design `docs/01` sections 1 to 3, 4.8, 5.4, 5.5, 6; `docs/03`;
spike results `docs/15`, `16`, `18`.

### 03 Workflow language

Owns the plan schema and its semantics.

| Section | Content |
|---|---|
| 1 Document shape | `apiVersion`, metadata, parameters, artifact types, defaults, steps |
| 2 Steps | Common fields; the three forms; ids; `protected` |
| 3 Outcomes | Declared outcomes and classes; engine-produced outcomes; fixed outcomes of engine actions |
| 4 Routing | Per-step routes; guarded routes; **plan-level route defaults**; list order as the default next step; directives `end`, `escalate`, `resume`; re-entry runs a step again, with `previous` and `executions` |
| 5 Expressions | CEL, the allowed functions, bounds, namespaces. How repository facts reach routing without breaking determinism (review section 6, item 6) |
| 6 Parameters and bindings | Declaration, binding, task rendering |
| 7 Artifact contracts | `produces` per outcome and `consumes`, including `from: parent`; validation with correction turns |
| 8 Parallel, chains and fan-out | Joins; ordered chains; **fan-out over repositories within one run** (review section 6, item 4) |
| 9 Child runs | Starting children; **parent-to-child data passing** (inputs and parent artifacts, from FF); the `replan_parent` signal (RW6); mapping a child's end status to the parent |
| 10 Patches | Operations, `base_revision`, `resume_at`, who may propose what; deterministic expansion of a list into steps |
| 11 Fragments | Reusable step groups from the library (for example "standard review"), expanded at load so validation sees the result (RW5) |

Sources: first design `docs/09`, `docs/10`, `docs/13`, `docs/14`; FF
`step-types.md` (outcomes, routing, `on_exhausted`, consumes), `workflow-templates.md`
(inputs, child workflows), `engine-flexibility.md` item 1 (input precedence).

### 04 Engine mechanisms

Owns the step catalogue: every agent step, decision and engine action, with its
contract. Target: about 15 to 20 entries, down from 40.

| Section | Content |
|---|---|
| 1 Contract format | Inputs, capabilities, outcomes, idempotency, limits, error overrides, artifacts, audit |
| 2 The agent step | Role, task, skills, tools and toolsets (external MCP servers, from FF), write access, limits, resume, push behaviour, the **agent reporting contract** (how an agent states its outcome and artifacts), questions to a human (`ask_human`, from FF), run context for later steps, `after_changes` repository commands (from FF) |
| 3 Decisions | The one decision primitive: gates, auto-resolution within policy, choices, interventions, questions, breakpoints |
| 4 Composition | Child run, parallel |
| 5 Planning | Re-plan by agent; deterministic expansion |
| 6 Source control | Create branch, catch up, open or update a pull request, merge (engine or human), revert |
| 7 Waiting | One condition wait with providers (pull request state, checks, tracker state), from FF's `awaitCondition` |
| 8 Testing and analysis | Run test tiers (sandbox or CI delegation); integration tests across repositories; static analysis; **baselines** (from FF) |
| 9 Evidence and validation | Engine-only evidence; the validator plug point; validate artifacts; the trace validator in the standard set (RW7) |
| 10 Findings | Record findings from an artifact; collect findings from an external reviewer; query the ledger from routes. No triage or readiness policy (RW4, RW5) |
| 11 Tracker and notifications | Declarative tracker transitions on any step (from FF) plus one tracker action; notify |
| 12 Artifacts | Compose, import, publish |

Sources: first design `docs/06`; FF `step-types.md`, `validators.md`,
`verification.md`.

### 05 Guardrails and policy

Owns every engine guardrail and the policy model.

| Section | Content |
|---|---|
| 1 Safety floors | The authoritative list (P2): credentials, secrets, attempt ceilings, the default-branch merge cap, evidence types, record integrity |
| 2 The default-branch merge cap | One setting with an organisation lock; read only from places no agent can edit; enforced at validation and at merge; code host protection as the final fallback. Simplified from the first design's seven settings (review section 5.5) |
| 3 Policy model | Approval, attempts, steering and override roles, chat. Keyed by decision type pattern and branch role, not by level name (review section 3.4) |
| 4 Approval | The template requests (for example auto-approval); the organisation caps and says who may approve (review section 3.3) |
| 5 Overrides and bypass | What a human may change at run time, by role; bypassing protected steps (RW8); what is never overridable |
| 6 Agent proposals | When an agent's patch or override becomes a pending decision |
| 7 Decision types | Declared by the library with a default approval rule; organisation overrides by pattern (review section 6, item 3) |
| 8 Effective policy view | How validation shows an author the resolved policy for each merge and decision |

Sources: first design `docs/01` sections 4.6, 5.1, 5.2; `docs/07` sections 2,
4, 7; `docs/09` sections 11.0, 14, 15.4, 15.5; FF `running-and-controlling.md`.

### 06 Providers

Owns provider interfaces and contracts. Carried over, with these changes:
review becomes "collect findings" for external reviewers (the standard reviewer
is an agent step, RW5); a toolset provider for external MCP servers; a
validator interface.

| Section | Content |
|---|---|
| 1 Rules every provider follows | Base interface, capabilities, classified errors, every success mapped, no waiting, transport-neutral, contract tests |
| 2 to 15 | One section per provider: sandbox, agent runtime, VCS, code host, CI, tracker, findings source, validator, toolset, artifact store, record store, secrets, notifications, credential broker, session surface |
| 16 Packaging | In-process packages; distribution repository instead of a fork |

Sources: first design `docs/04`, `docs/05`.

### 07 Configuration

Owns settings, their layers and the three deployment profiles. States settings
only; their meaning is in the owning document (P12).

| Section | Content |
|---|---|
| 1 Layers and precedence | Installation, organisation (with team overrides), repository, run |
| 2 Installation | Temporal, workers, provider instances |
| 3 Organisation | Policy settings (meaning in 05), role mapping to runtimes and models |
| 4 Repository manifest | Environment, setup, test tiers, static analysis, CI mapping, integration environment |
| 5 Run request | Template, parameters, repositories, overrides |
| 6 Profiles | Solo, small company, enterprise |
| 7 Validation at startup and run start | — |

Sources: first design `docs/07`; FF `configuration-reference.md`.

### 08 Library

Owns library content, its sources, and the standard library.

| Section | Content |
|---|---|
| 1 Content types | Templates, fragments, tasks, skills, resources, roles, artifact types, validators, decision types, prompts |
| 2 Manifest | Name, version, inputs, requirements, entry points (from FF `library.yaml`) |
| 3 Sources | A directory as a first-class source; packages as an optional distribution format (review section 5.4) |
| 4 Layers | Base, organisation, repository additions, run override |
| 5 The lock and mid-run override | Pinned by hash at run start; override for future steps, audited |
| 6 The standard library | Templates (ticket, epic, project), fragments (standard review, fix loop, catch-up and merge, delivery), roles, the traceability artifact types and validator, the default policies moved out of the engine (RW4) |

Sources: first design `docs/11`; FF `concepts.md` (the library),
`engine-flexibility.md` items 2 and 3, `engine-flexibility-2.md` item 6.

### 09 Validation and testing

| Section | Content |
|---|---|
| 1 The blocking rule | From P11 |
| 2 Phases and checks | Load, binding, run start, patch, step start |
| 3 Workflow tests | Recording and fake providers; workflow test files |
| 4 Interpreter tests | Replay of recorded histories; determinism rules for workflow code |
| 5 Provider contract tests | — |

Sources: first design `docs/12`.

### 10 Decisions and risks

| Section | Content |
|---|---|
| 1 Decision register | Every decision: choice, reason, evidence, status. Folds in the first design's records (`archive/docs/02`, `03`, `14`), the review answers (`archive/docs/reviews/`) and RW1 to RW9 |
| 2 Risk register | Carried over from `archive/docs/08`, updated |
| 3 Evidence index | Which spike evidenced which claim |
| 4 Spike plan | What still needs a spike: the lock-free merge, the new continue-as-new safe point, `ask_human` across a worker restart, Windows |

---

## 2. Writing order

Each document depends only on documents before it.

| Order | Document | Why here |
|---|---|---|
| 1 | 01 Concepts | Every other document uses its terms |
| 2 | 03 Workflow language | Defines what mechanisms must support |
| 3 | 04 Engine mechanisms | Implements the language |
| 4 | 05 Guardrails and policy | Bounds the mechanisms |
| 5 | 02 Engine architecture | Mostly carried over; updated for the changes above |
| 6 | 06 Providers | Serves the mechanisms |
| 7 | 08 Library | Needs the language and mechanisms; includes the standard library |
| 8 | 07 Configuration | Collects settings defined elsewhere |
| 9 | 09 Validation and testing | Collects checks defined elsewhere |
| 10 | 10 Decisions and risks | Collects decisions made along the way |

---

## 3. Coverage

### 3.1 Feature Factory facilities

| FF facility | Lands in |
|---|---|
| Free outcome names on agent and human steps | 03 section 3 |
| `continue`, `escalate`, `on_exhausted` defaults | 03 section 4 |
| `goto` and `redirect` with `clean_up` (reset the working state when redoing work) | 03 section 4; whether `clean_up` is still needed with hidden references is open question 5 |
| Interventions with retry, goto, redirect | 04 section 3 |
| `ask_human` questions | 04 sections 2 and 3 |
| Breakpoints | 04 section 3; 05 section 5 |
| Protected and mandatory steps | 03 section 2; 05 section 5 (mandatory dropped, `00-principles.md` open question 1) |
| Produces per outcome, consumes with `from: parent` | 03 section 7 |
| Artifact sets, revisions, review verdicts, ratification | 01 section 5; 04 section 12 |
| Engine-only evidence definitions | 01 section 5; 04 section 9 |
| Validators and `validateArtifacts` | 04 section 9; 06 |
| Requirement trace: VRs, ACs, `supports`, `owner`, evidence claims, trace report | 01 section 8; 04 section 9; 08 section 6 |
| Test and static analysis baselines; carried results across a merge | 04 section 8 |
| `after_changes` repository commands | 04 section 2 |
| Run context (`context.md`) and `context: none` | 04 section 2 |
| Roles orchestrator, worker, validator | 08 section 6 (as standard roles over the write-access mechanism) |
| Toolsets and external MCP servers | 04 section 2; 06 |
| Library manifest, inputs, resources, entry points | 08 sections 2 and 6 |
| Input precedence and child inheritance | 03 sections 6 and 9 |
| Budget on descendant runs (`max_work_items`) | 05 section 3 |
| `approvals: auto` as one switch for a run tree | 05 section 4 |
| Interactive planning sessions and planning repositories | 04 section 3; 06 (session surface) |
| `awaitCondition` | 04 section 7 |
| Declarative tracker transitions; Jira tools for planners | 04 sections 2 and 11 |
| Notifications on domain events | 04 section 11; 06 |
| `awaitCopilotReview` | 06 (findings source) |
| Publication report: which repositories really merged | 04 section 6 |

### 3.2 Review findings

From `archive/review-vs-feature-factory.md`.

| Finding | Lands in |
|---|---|
| 3.1 Fixed outcome vocabulary | 03 section 3 |
| 3.2 Review and findings policy in the engine | 04 section 10; 08 section 6 |
| 3.3 Approval only in the organisation | 05 section 4 |
| 3.4 Configuration keyed by level names | 05 section 3; 07 |
| 4 Missing FF facilities | Section 3.1 above |
| 5.1 Verbosity | 03 section 4 |
| 5.2 Forty step kinds | 04 |
| 5.3 Review provider as a second agent path | 04 section 10; 06 |
| 5.4 Library packaging | 08 section 3 |
| 5.5 Policy settings spread across layers | 05 sections 2 and 8 |
| 6.1 Continue-as-new safe point | 02 section 4 |
| 6.2 `replan_parent` join semantics | 03 section 9 |
| 6.3 Decision type registry | 05 section 7 |
| 6.4 One repository per ticket | 03 section 8 |
| 6.5 Parent-to-child data | 03 section 9 |
| 6.6 Repository facts in routing | 03 section 5 |
| 6.7 Hook endpoint in the API | 02 section 5 |
| 6.8 Engine working clones | 02 section 6 |
| 6.9 Lock-free merge unspiked | 10 section 4 |
| 7 Inconsistencies | Resolved by writing each fact once |
| 8 Duplication | P12 |

---

## 4. Open questions raised by the outline

None open. All seven were answered as recommended by the product owner on
2026-10-31; they are decisions RW12 to RW18 in `PLAN.md`.

### Answered

| # | Question | Answer |
|---|---|---|
| 1 | One naming convention for plans and configuration? The first design used snake_case for plans and camelCase for configuration | snake_case everywhere. Configuration is now written fresh, so the reason for camelCase (already written) is gone |
| 2 | Step order: is the list order of steps meaningful (default next step, as in FF), or is the plan purely a graph? | List order gives the default next step for a `success` outcome. Explicit routes override it |
| 3 | Is `ask_human` in version 1? The agent waits inside its step; the sandbox and session survive a worker restart (first design, resumable agent steps) | Yes. It is the main FF facility for unattended runs, and the first design's resume model covers it |
| 4 | Is delivery (deploying across repositories in order) in version 1, beyond merging the delivery pull requests? | Merging in declared order and reporting which repositories merged, yes. Deploy triggering, no |
| 5 | Does `clean_up` (resetting the working state when a route goes back to redo work) still need to exist? | Yes, as a route option: going back to re-plan should start from the parent branch, not the failed attempt's commits |
| 6 | Where is the agent hook endpoint served: the API, or the agent worker? | The agent worker, so an API outage does not pause agents. Not inside the sandbox, where the agent could reach it. Decide in 02 |
| 7 | Do interactive human sessions (a person working in their own agent tool, with pre-submit checks) stay in version 1? | Yes, as in both prototypes |
