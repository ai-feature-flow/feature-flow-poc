# Decisions and risks

Status: draft, 2026-10-31.

This document owns the decision register, the risk register, the evidence index
and the spike plan. Decisions keep the identifier they were given where they
were made, so earlier references still resolve:

| Prefix | Origin |
|---|---|
| A, S, DP | Assumptions, suggestions and discussion points of the first design (`archive/docs/02`) |
| Q | The brief's open questions, decided in `archive/docs/03` |
| L | Workflow language decisions (`archive/docs/14`) |
| DR | Design requirements from the spikes (`archive/docs/08` section 1.3) |
| RW | Decisions taken for the rewrite (`docs/PLAN.md` section 3) |
| LOG | Judgement calls awaiting the product owner's review (`docs/LOG.md`) |

Status is **kept** (carried into this design unchanged), **changed** (carried
with a change, and by what), or **superseded** (replaced).

---

## 1. Decision register

### 1.1 Engine and platform

| Id | Decision | Status | Where now |
|---|---|---|---|
| Q1 | .NET, not Python | Kept | `docs/02` |
| Q2 | Tiers that need Docker are delegated to CI by default; a privileged container only in the solo profile | Kept | `docs/07` section 6 |
| Q3 | One repository file, `feature-flow.yaml`, which may reference a `devcontainer.json` | Changed by LOG 44 and 46: FF's test manifest keys merged in; `branches` and `merge` sections removed | `docs/07` section 4 |
| Q4 | Artifact store plus record store; Git is not a store | Kept | `docs/02` section 5 |
| Q5 | People's decisions arrive as Temporal Updates through the API, which authorises them | Kept | `docs/02` section 7 |
| Q6 | Plan as data in workflow state, continue-as-new at a measured threshold | Changed by LOG 31: new safe-point rule | `docs/02` section 3 |
| Q7 | In-process provider packages; a distribution repository instead of a fork | Kept | `docs/06` section 4 |
| Q8 | The engine uses a GitHub App (or equivalent) identity; agents get per-step read-only tokens | Kept, strengthened by DR13 | `docs/06` section 3.13 |
| Q9 | Windows sandboxes later; capability routing and CI delegation in version 1 | Kept | `docs/02` section 4.4 |
| DP3 | Child workflows only where a step has its own lifecycle (now: `run.child`) | Kept | `docs/02` section 2.2 |
| DP6 | Most specific layer wins, except safety floors | Kept | `docs/00` section 3, `docs/05` |
| DP8 | The API authorises; the workflow trusts the API | Kept | `docs/02` section 7 |
| DP9 | Temporal is required; no second execution backend | Kept | `docs/02` |
| DP10 | Each level's review covers its whole branch against its parent | Kept | `docs/08` section 6.3 |
| DP11 | The agent runtime runs inside the sandbox (pattern C) | Kept | `docs/02` section 4.1 |
| S1 | Task queues by capability | Kept | `docs/02` section 1.1 |
| S2 | One patch path | Kept | `docs/03` section 10 |
| S3 | Capability negotiation at run start | Kept | `docs/09` check 21 |
| S4 | Idempotency keys and hidden markers | Kept | `docs/04` section 1 |
| S5 | Contract tests per interface | Kept | `docs/06` section 1 |
| S6 | Fingerprints without line numbers | Kept | `docs/01` section 6 |
| S8 | Replay tests in CI | Kept | `docs/09` section 4.1 |
| S9 | `unauthenticated` and `forbidden` non-retryable after a short budget | Changed by LOG 35: then an intervention | `docs/06` section 1.1 |
| S12 | CI delegation is a first-class path | Kept | `docs/04` section 9.1 |
| S13 | "Run stuck" notifications | Kept | `docs/06` section 3.10 |
| S14 | Plan schema versioned separately from the interpreter | Kept | `docs/02` section 2.1 |
| A5 | One repository per ticket is the common case | Changed: `for_each` over repositories (LOG 12) | `docs/03` section 8.2 |
| A7 | Delivery triggers and gates; it does not deploy | Kept, narrowed by RW15 | `docs/04` section 2 |

### 1.2 Design requirements from the spikes

| Id | Requirement | Status | Where now |
|---|---|---|---|
| DR1 | Model credentials never enter the agent's environment | Kept as floor F6 | `docs/05` section 1 |
| DR2 | The default-branch setting is enforced at validation and merge time, reading the code host | Changed by LOG 26: organisation-only setting | `docs/05` section 2 |
| DR3 | Branch protection is configured by an identity the engine does not hold | Kept, in floor F5 | `docs/05` section 1 |
| DR4 | Review verdicts are unstable; the ledger is the source of truth | Kept; ledger policy moved to the library (RW4) | `docs/01` section 6, `docs/08` section 6.5 |
| DR5 | Engine runs on Windows, macOS or Linux containers | Kept | `docs/02` section 4.4 |
| DR6 | A run without an end route never reports success | Kept as floor F9; LOG 9 adds a default end after the last step | `docs/03` section 4.3 |
| DR7 | Start on first completion; interpreter changes versioned | Kept | `docs/02` section 2.1 |
| DR8 | Every documented success response mapped | Kept | `docs/06` section 1 |
| DR9 | Resume never depends on the crashed worker's memory | Kept | `docs/04` section 3.2 |
| DR10 | Attempt ceilings checked on every execution | Kept as floor F4 | `docs/01` section 4 |
| DR11 | A parent's review includes its children's ledgers | Kept | `docs/01` section 6 |
| DR12 | Re-entry runs a step again | Kept | `docs/03` section 4.2 |
| DR13 | Agents never hold a credential that can push | Kept as floor F5 | `docs/05` section 1 |
| DR14 | The interpreter sets its carried state in the workflow initialiser, because Temporal can deliver signals and updates before the run method starts (SP1) | New | `docs/spikes/sp1-results.md` section 5.4 |
| DR15 | A hand-over waits for running signal and update handlers to finish before it continues-as-new (SP1) | New | `docs/02` section 3 |
| DR16 | The agent runtime's MCP tool timeout exceeds `questions.timeout`, and the hook command's timeout exceeds the hook client's hold (SP2) | New | `docs/spikes/sp2-results.md` section 5.4 |
| DR17 | The engine creates the step's output folder before the agent starts; a runtime's file tool may not create folders (SP3) | New | `docs/spikes/sp3-results.md` section 5.4 |

### 1.3 Workflow language

| Id | Decision | Status | Where now |
|---|---|---|---|
| L1 | One schema for template and plan | Kept | `docs/03` section 1 |
| L2 | CEL for expressions; no artifact content in expressions | Kept | `docs/03` section 5 |
| L3 | Domain patch operations | Changed by LOG 14: six operations | `docs/03` section 10.2 |
| L4 | Roles in the library; privilege from the role | Kept | `docs/01` section 2.2 |
| L5 | One reference format; rendered tasks stored by hash | Kept | `docs/08` section 1.1 |
| L6 | Typed artifact contracts with declared compatibility | Kept, extended with evidence types and verdicts | `docs/03` section 7 |
| L7 | NuGet packages plus an overlay directory | Superseded by LOG 38: directory, git and package sources | `docs/08` section 3 |
| L8 | Mid-run library override, separate from patches | Kept; now works on every run | `docs/08` section 5.2 |
| L9 | Child plans by deterministic expansion into a patch | Kept | `docs/03` section 10.4 |
| L10 | Validation phases and one blocking rule | Kept | `docs/09` |
| L11 | Replay and re-drive as separate test mechanisms | Kept | `docs/09` section 4.1 |
| L12 | Templates do not carry who may approve | Changed by RW9 and LOG 27: a template may request auto-resolution, which the organisation caps | `docs/05` section 3 |

### 1.4 Rewrite decisions

RW1 to RW18 are listed in `docs/PLAN.md` section 3 and applied throughout.
LOG 1 to LOG 57, the judgement calls made while writing the drafts, were
approved by the product owner on 2026-11-01 and are decisions. LOG 58, from
SP1 (answers accepted during a continue-as-new drain), and LOG 59 and 60, from SP2
(the sandbox client owns question ids and never returns an infrastructure error
to the agent), were approved the same day. They stay in
`docs/LOG.md`, which owns them; this register does not copy them (P12).

---

## 2. Risk register

| # | Risk | Mitigation | State |
|---|---|---|---|
| R1 | History limits on long runs | References only; capped summaries; continue-as-new at a measured threshold; plan size check | Mitigated, measured (Spike 1) |
| R2 | Non-determinism in the interpreter | Pure-function discipline; replay tests | Mitigated (Spikes 1 and 3) |
| R3 | A decision lost during continue-as-new | Answers recorded by the handler; the hand-over waits for running handlers (LOG 58) | Mitigated (SP1) |
| R4 | Unstable review verdicts | Ledger; evidence-backed findings; separate review angles | Handled by design; the new reviewer not yet run |
| R5 | Sandbox does not match CI | Tiers declared per repository; CI delegation | Mitigated (Spike 2) |
| R6 | Credential exposure | Floors F2, F5, F6; per-step tokens | Partly mitigated; F5 and F6 not yet built |
| R7 | An agent weakens protected steps | F3 in the patch path | Mitigated (Spike 1) |
| R8 | Runs stall on people | Unbounded durable waits with reminders; stuck-run notifications | Mitigated in mechanism |
| R9 | An organisation's distribution drifts from upstream | Configuration, providers and library overrides; contract and library tests | Residual |
| R10 | Concurrent work in one repository conflicts | Catch up, re-test if changed, merge refused if the parent moved; merge allowance | Lock-free form not spiked |
| R11 | A long agent step restarts from scratch | Sandbox outlives the worker; durable resume state; hidden reference | Mitigated, with the Spike 3 gap fixed in design |
| R13 | Run cost unknown | Cost recorded per step | Mitigated (Spike 2) |
| R14 | Pattern C on Windows | Claude Code hooks fired on Windows | Partly answered |
| R15 | A resumed agent makes no progress | Progress rule | Mitigated (Spike 1) |
| R16 | Repository settings go stale | Manifest read per step | Mitigated (Spike 2) |
| R17 | A check appears first after submission | Pre-submit checks with parity | Partly evidenced (Spike 3) |
| R18 | A long command is lost with the worker | Start and poll; re-attach by handle | Mitigated (Spike 1) |
| R19 | An unreachable hook endpoint reads as a denial | Hold and retry; then a resumable stop | Mitigated (Spike 1; re-tested on the agent worker in SP2) |
| R20 | Polling inflates history | Poll inside one activity | Mitigated (Spike 1) |
| R21 | The continue-as-new rule with detached children and carried decisions loses a child's end or an answer | Children signal the parent's workflow id; the parent confirms by reading; answers carried as state | Mitigated (SP1) |
| R22 | `ask_human` across a worker restart: the agent waits, the worker goes, the answer is lost | Question state in the workflow and record store; stateless replicas; the sandbox client owns the question id and reconnects (LOG 59) | Mitigated (SP2) |
| R23 | Agent runtimes do not call the engine's MCP tools reliably (reporting, questions, patches) | File fallback for reporting; contract tests per runtime | Mitigated for reporting and questions on Claude Code and Copilot CLI (SP3); patches not measured |
| R24 | **New.** Policy now lives in the standard library, so a weak `std` gives weak delivery | `std` is versioned and tested like engine code; its tests run in engine CI | Residual |
| R25 | **New.** Fragments and defaults make the resolved plan harder to see than what the author wrote | The readable form shows the resolved plan; validation runs on it | Design |

R12 (non-determinism not caught at runtime) is merged into R2.

---

## 3. Evidence index

Where the claims this design relies on were evidenced.

| Claim | Evidence |
|---|---|
| An interpreter on Temporal survives worker crashes and applies re-plans mid-run | Spike 1, `archive/docs/15` sections 3 and 4 |
| History per step and carried state per step | Spike 1, `archive/docs/15` section 6.1 |
| One polling activity costs about 11 events | Spike 1, `archive/docs/15` section 6.2 |
| Crash recovery time is dominated by heartbeat detection | Spike 1, `archive/docs/15` section 6.4 |
| Cel.NET can be restricted, is deterministic and is fast enough | Spike 1, `archive/docs/15` section 9.2 |
| A real ticket run end to end with a real agent and code host | Spike 2, `archive/docs/16` sections 2 and 12 |
| CI delegation of a Docker tier | Spike 2, `archive/docs/16` section 2 |
| Hook behaviour of Claude Code and Copilot CLI | Spike 2, `archive/docs/16` sections 4 and 12 |
| Short-lived, scoped credentials from the broker | Spike 2, `archive/docs/16` section 2 |
| Review verdicts differ on unchanged input | Spike 2, `archive/docs/16` section 2 |
| Concurrent tickets, catch-up, re-test on change, conflicts resolved by an agent | Spike 3, `archive/docs/18` section 2 |
| An epic-level review catches cross-ticket defects ticket reviews miss | Spike 3, `archive/docs/18` section 3 |
| Attempt ceilings must be checked on loop re-entry | Spike 3, `archive/docs/18` section 4 |
| Continue-as-new with running children and open decisions loses nothing | SP1, `docs/spikes/sp1-results.md` section 5 |
| An open question survives the loss of the agent worker replica serving it; a hook call during an outage holds rather than denies | SP2, `docs/spikes/sp2-results.md` section 5 |
| An agent given an infrastructure error by an engine tool improvises a result | SP2, `docs/spikes/sp2-results.md` section 5.3 |
| Claude Code and Copilot CLI report, correct, fall back to a file, wait on a question and stop when told, through MCP | SP3, `docs/spikes/sp3-results.md` section 5.1 |
| Copilot CLI fires `PreToolUse` on every tool call with a first-party model | SP3, `docs/spikes/sp3-results.md` section 5.3 |
| The delivery model (library, free outcomes, traceability, engine evidence) works on a real feature | FF: 2 epics, 16 tickets, 3 repositories (`brief.md` section 3) |

---

## 4. Spike plan

What must be evidenced before the parts that depend on it are built.

| # | Spike | Proves | Risks |
|---|---|---|---|
| SP1 | Continue-as-new in a parent with running children, an open decision and a gate answered during the hand-over | The new safe-point rule loses nothing. **Done 2026-11-01: holds** (`docs/spikes/sp1-results.md`) | R21, R3 |
| SP2 | `ask_human` with Claude Code: a question open across a worker restart, answered afterwards | Question durability on the agent worker. **Done 2026-11-01: holds** (`docs/spikes/sp2-results.md`) | R22, R19 |
| SP3 | The agent reporting contract over MCP on Claude Code and Copilot CLI, including correction turns | Runtimes use the engine tools reliably. **Done 2026-11-01: holds** (`docs/spikes/sp3-results.md`) | R23 |
| SP4 | Lock-free merging with three or more concurrent tickets and forced races | `base_moved` loops and the allowance behave | R10 |
| SP5 | Model proxy: an agent completes a step with no model key in its environment | Floor F6 can be met with both runtimes | R6 |
| SP6 | Session resume on Windows (W0 from the first design) | Pattern C on Windows | R14 |

SP1 to SP3 change how the engine is built and should run first.

---

## 5. Open questions

None.
