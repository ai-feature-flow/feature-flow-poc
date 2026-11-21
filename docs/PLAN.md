# Design rewrite plan

Status: design complete 2026-11-01. Version 1 built: milestones M0 to M8 of
`docs/11-build-plan.md` done by 2026-11-05; the latest handover is
`docs/build/handover-2026-11-05-v1.md`. Next phase: `docs/12-build-plan-phase-2.md`,
drafted 2026-11-21; A1 to A5 built 2026-11-21 and A6 2026-11-22, so alpha is complete apart from the product owner's A4 live run; its follow-ups batch built 2026-11-22; R1 next. Started 2026-10-31. This file is the
resume point: a new session reads it first, then the status table in section 6
and the latest handover.

## 1. Goal

A clean, comprehensive design for Feature Flow that:

- keeps the engine architecture of the first design (Temporal interpreter,
  plan as data, providers, sandboxes, stores), which the three spikes evidenced;
- brings over every facility that worked in Feature Factory (FF), the local
  TypeScript prototype;
- fixes the shortcomings found in FF during the hackathon and in the first
  Feature Flow design;
- applies the recommendations of the design review
  (`archive/review-vs-feature-factory.md`).

## 2. Principles the rewrite applies

Stated fully in `docs/00-principles.md`. In short:

| Principle | Meaning |
|---|---|
| Guardrails in the engine, policy in the workflow | The engine provides mechanisms and safety floors. Everything else is a workflow choice, with standard defaults in the library |
| The guardrail test | A rule belongs in the engine only if it is needed for safety or for a trustworthy record, whatever the workflow |
| Humans may override anything within organisation limits | An authorised human can amend, bypass or override, including protected steps. Safety floors are never relaxed. Every override is audited |
| Simplicity and repeated patterns | One mechanism per concern, used the same way at every level |
| Pluggability | Every external system behind a provider interface with capabilities and contract tests |
| One fact, one place | Each rule has one owning section. Elsewhere: one sentence and a reference |

## 3. Decisions taken for the rewrite

Product owner, 2026-10-31. These stay here; `docs/10` section 1.4 refers to
them.

| # | Decision |
|---|---|
| RW1 | Rewrite the design. The first design moves to `archive/docs/` (with `git mv`, so history is kept). Spike code stays in `spikes/` |
| RW2 | Write the principles document and the full outline first, for review, before any other document |
| RW3 | Agent and human steps declare their own outcome names. Each declared outcome may carry an optional `class` (`success`, `failure`, `blocked`) for reporting and default routing. The engine still produces `attempts_exhausted` itself. Engine actions keep fixed outcome sets |
| RW4 | Review and findings policy moves from the engine to library defaults: the regression test per fix, escalation after repeated fixes, the merge bar, "demonstrated bugs block". Standard templates mark those steps protected |
| RW5 | The standard reviewer is an `agent.run` with a reviewer role, plus an engine action that records findings into the ledger. A library fragment ("standard review") hides the detail from template authors. External reviewers plug in behind a "collect findings" action |
| RW6 | `replan_parent` is a signal a child sends while it continues or waits. The parent's join counts the branch only when the child ends |
| RW7 | Requirement traceability in version 1: engine-only evidence artifacts and a validator plug point are engine mechanisms; trace rules (requirement and acceptance criteria schemas, `supports`, `owner`) are library content |
| RW8 | An authorised human's steering may bypass protected steps, audited. It may never relax a safety floor (`docs/05` section 1) |
| RW9 | The rest of the review's recommendations are accepted as direction: route defaults and an `escalate` directive; parent-to-child data passing; per-repository fan-out; one human-decision primitive; a reduced step catalogue; a directory library as a first-class source |
| RW10 | No "mandatory" step marking (a protected step whose bypass needs a second approver) in version 1 |
| RW11 | Repository facts are always read fresh at each step; a workflow cannot pin them |
| RW12 | snake_case for every key, in plans, library content and configuration |
| RW13 | The list order of steps gives the default next step for an outcome of class `success`. Explicit routes override it |
| RW14 | `ask_human` (an agent asks a person a question during its step and waits) is in version 1 |
| RW15 | Delivery in version 1: merging delivery pull requests in a declared order and reporting which repositories merged. No deploy triggering |
| RW16 | `clean_up` stays as a route option: a route back to redo work can reset the work branch to the commit the target step started from |
| RW17 | The agent hook endpoint is served by the agent worker, not the API and not inside the sandbox. Detail in `02` |
| RW18 | Interactive human sessions (a person working in their own agent tool, with pre-submit checks) are in version 1 |

Product owner, 2026-11-21, on the design review and the console design for the
next phase. `docs/12` section 1.2 refers to them.

| # | Decision |
|---|---|
| NP1 | Guiding principle: the engine runs anywhere from one developer on a laptop to a corporate cloud install. Authentication is pluggable or configurable; a fork with bespoke handlers is acceptable (`docs/00` P13) |
| NP2 | Two release stages: alpha, the product owner running locally, first; then the first full release, which includes the enterprise profile |
| NP3 | Console: Design A of the console design, TypeScript with a Node build, served by the API at `/console`, with hash routes. The build tool is a judgement call (LOG 344) |
| NP4 | Console sign-in: none or a local token in alpha; OIDC with PKCE (Okta) for the enterprise profile |
| NP5 | The console's first slice includes the steering controls (G10, G11) and live updates by server-sent events (G8) |
| NP6 | Any viewer of a run may read its agent transcripts |
| NP7 | F2: the `<level>.merge_action` decision is built |
| NP8 | F7: the enterprise profile (Temporal Cloud connection, TeamCity, SonarCloud) is required for the first full release, not alpha. F19: toolsets are not in version 1; the settings accepted but unused are refused |
| NP9 | The review's other recommendations are accepted, F14 as a milestone item. Ways of working: the default test set on a pull request, the full set by a `full-ci` label; targeted tests plus the default suite while building; one review per pull request; live runs only where a done-when needs one |
| NP10 | Product owner, 2026-11-21, answering `docs/12` section 7: questions 1, 3, 5, 6 and 7 accepted as proposed: `api.auth.mode: none` on loopback only (LOG 333); engine simplification (A2) before oversight (LOG 330); `dry_run` kept, a lockable `merge.require_tested` added, `notify.send` removed (LOG 342) |
| NP11 | Question 2: backward compatibility returns at the first full release, not alpha. Question 8: assume the Okta tenant can register a public browser client for the console; verify before R2 starts |
| NP12 | Question 4, overridden: a pull request merged on the code host before the `<level>.merge_action` decision is answered is accepted and the run continues. The decision closes as answered on the code host, the timeline records the code host login and that the decision was not given through Feature Flow, and no intervention is raised. Branch protection governs merges made on the code host; Feature Flow's approval rules govern merges requested through it (LOG 341) |

## 4. Document structure

Each document owns the facts listed and does not restate another's.

| # | File | Owns |
|---|---|---|
| 00 | `00-principles.md` | Principles, the guardrail test, the override rule, glossary |
| 01 | `01-concepts.md` | The domain model: run, level, plan, step, outcome, route, artifact, evidence, finding, decision, intervention, attempt |
| 02 | `02-engine-architecture.md` | Components, Temporal interpreter, workers, sandboxes, agent pattern, stores, control plane, observability |
| 03 | `03-workflow-language.md` | Plan schema, routing and defaults, expressions, parameters, child runs and data passing, patches |
| 04 | `04-engine-mechanisms.md` | The step catalogue: agent step, human decision, engine actions, findings and evidence actions, validators |
| 05 | `05-guardrails-and-policy.md` | Safety floors, the policy model, approval, overrides and bypass, steering, who may do what |
| 06 | `06-providers.md` | Provider interfaces, capabilities, contracts, error classification |
| 07 | `07-configuration.md` | Layers, schemas, profiles. Settings only; policy semantics stay in 05 |
| 08 | `08-library.md` | Library format and sources, lock, mid-run override, and the standard library's templates and fragments |
| 09 | `09-validation-and-testing.md` | Validation phases and checks, workflow testing, interpreter replay tests |
| 10 | `10-decisions-and-risks.md` | Decision register, risk register, spike plan, evidence index |
| 11 | `11-build-plan.md` | Version 1 build: code location and layout, build order, milestones M0 to M8, carry-over from spikes |
| 12 | `12-build-plan-phase-2.md` | Phase 2 build: release stages, alpha and first full release milestones, and where each design review finding and console gap is built |

Reviews from the product owner go in `docs/reviews/`, as before.

## 5. Sources

Paths in all design documents are relative to the repository root.

| Source | Use |
|---|---|
| `archive/docs/01` to `20` | The first design. Engine architecture is carried over; workflow surface is redesigned |
| `archive/docs/15`, `16`, `18` | Spike results: the evidence. Cited, not restated |
| `archive/docs/reviews/` | Earlier product owner answers. Folded into the decision register |
| `archive/review-vs-feature-factory.md` | The review this rewrite applies |
| `brief.md` | The original brief. Still the requirements input |
| `../feature-factory/docs/` | FF user guide: `concepts.md`, `step-types.md`, `verification.md`, `artifacts.md`, `validators.md`, `workflow-templates.md`, `running-and-controlling.md`, `planning.md` |
| `../feature-factory/docs/design/` | FF design notes, especially `engine-flexibility.md` and `engine-flexibility-2.md` |

## 6. Status

| Step | State |
|---|---|
| Archive the first design | Done 2026-10-31 |
| `00-principles.md` and `OUTLINE.md` | Reviewed and accepted 2026-10-31 (RW10 to RW18) |
| Write 01 to 10 | First drafts done 2026-10-31, overnight, without the product owner. Judgement calls are in `LOG.md` |
| Consistency pass over 00 to 10 | Done 2026-10-31: an independent review found 39 issues, all fixed (`LOG.md` entries 49 to 56) |
| Product owner review of the drafts and `LOG.md` | Done 2026-11-01: approved as it stands; LOG 50 reversed (an administrator may grant beyond the hard ceiling) |
| Update `CONTRIBUTING.md` paths to the new structure | Done 2026-10-31 |
| Spike SP1 (`docs/10` section 4): continue-as-new with running children, open decisions and answers during the hand-over | Done 2026-11-01: the rule holds. Results in `docs/spikes/sp1-results.md`; one design change, LOG 58 (accept answers during the drain), approved |
| Spike SP2: `ask_human` across an agent worker restart | Done 2026-11-01: holds. Results in `docs/spikes/sp2-results.md`; two design changes, approved: LOG 59 (the sandbox client owns question ids) and LOG 60 (it never returns an infrastructure error to the agent) |
| Spike SP3: the agent reporting contract over MCP on Claude Code and Copilot CLI | Done 2026-11-01: holds on both. Results in `docs/spikes/sp3-results.md`; no LOG entries; DR17 added (the engine prepares the output folder) |
| Spike SP4: lock-free merging under forced races | Done 2026-11-01: safe. Results in `docs/spikes/sp4-results.md`; one design change, approved: LOG 61 (a separate race allowance for `base_moved` loops); DR18 added |
| Spike SP5: an agent step behind a model proxy | Done 2026-11-01 for Claude Code: holds. Results in `docs/spikes/sp5-results.md`; DR19 (the sandbox's own home) and DR20 (platform trust store) added; Copilot CLI not tested, for want of an API key |
| Spike SP6: session resume on Windows | Shelved by the product owner, 2026-11-01: low risk, version 1 has no Windows sandbox, and development moves to a Windows machine soon, where it can be tested in passing |
| The standard library as files (`docs/08` section 6) | Done 2026-11-01, in `library/std/`: 3 templates, 9 fragments, 11 tasks, 7 roles, 13 artifact types. Not yet loaded by a validator. Formats the design left open are LOG 62 to 65, approved 2026-11-01 |
| Version 1 build plan | Written 2026-11-01 in `docs/11-build-plan.md`: layout, build order, milestones M0 to M8, carry-over from spikes. Judgement calls LOG 66 to 70, approved 2026-11-01. Open questions 1, 2, 3 and 5 answered 2026-11-01 (code in this repository; pull requests with required CI; LOG 62 to 65 approved; a minimal console in M7); question 4, the team deployment's object store and container platform, deferred |
| Product owner review of LOG 66 to 70 | Done 2026-11-01: approved |
| M0 to M4 (skeleton; plan loader and validator; interpreter core and re-drive; runnable engine with fake providers; agent steps in a sandbox) | Done 2026-11-02 to 2026-11-03, merged at `9279026`. Results in `docs/build/m0-results.md` to `m4-results.md`; LOG 71 to 90 approved; LOG 91 to 106 approved 2026-11-03, with 91 simplified (four runtime settings became fixed defaults) and 106 amended (time on open questions does not count toward `limits.timeout`; `questions.timeout` defaults to 24 hours). State, access and next steps in `docs/build/handover-2026-11-03.md` |
| SP1 scenario flake on CI | Done 2026-11-03 (pull request 3) |
| M5 (source control, testing and the first real ticket) | Done 2026-11-03, merged at `ba1f62f` (pull request 7): the done-when's live run merged a real ticket into a non-default parent branch on the fixture. Results in `docs/build/m5-results.md`; LOG 107 to 146 approved 2026-11-03. Next steps, ways of working and decision principles in `docs/build/handover-2026-11-03-m6.md` |
| M6 (findings, validators and levels) | Done 2026-11-04: the done-whens' live runs delivered an epic of three issue-tracked tickets into its parent branch, and a project over two repositories with the product owner's merges into `main` detected. LOG 139 (a wait held up a run's hand-over) is fixed. Results in `docs/build/m6-results.md`; LOG 148 to 200 approved by the product owner 2026-11-04. Second fixture `ai-feature-flow/feature-flow-example-client` created. Next steps in `docs/build/handover-2026-11-04-m7.md` |
| M7 (people, policy and steering) | Done 2026-11-05 (pull requests 16 to 22): run-time overrides with who may use each, organisation policy and people roles, OIDC, Slack notifications and chat answers, webhooks, a minimal console, interactive sessions with pre-submit checks, report-time artifact checks and a person triaging findings. The done-when's live run had a second authenticated person submit a project's requirements session. Results in `docs/build/m7-results.md`; LOG 201 to 261, awaiting review |
| M8 (team profile and operations) | Done 2026-11-05 (pull requests 25 to 32): the sandbox's network closed (M4 follow-up 12), compatibility code removed, a Postgres record store, an S3 artifact store, the payload codec, reconcile, the `git` library source, the Kubernetes pod sandbox, Copilot CLI, `role_mapping`, Jira, OpenTelemetry, stuck-run detection, host images, a k3d deployment, a distribution template and a `Cluster` CI job. The done-when's live run delivered an epic of three Jira tickets on k3d. Results in `docs/build/m8-results.md`; LOG 271 to 327, approved by the product owner 2026-11-07. **Version 1 is built** |
| After version 1: what to take from M8's consolidated follow-ups (Copilot quota first, follow-up 14) and a lighter CI while development goes on | Superseded 2026-11-21 by the next-phase plan: the M8 follow-ups the design review took up (Copilot quota as follow-up 14 in A5) and the CI plan are in its milestones and ways of working |
| Next-phase plan: design review (findings F1 to F21, L1 to L11) and console design (gaps G1 to G14), product owner decisions NP1 to NP9 (answers to its open questions NP10 to NP12) | Drafted 2026-11-21 in `docs/12-build-plan-phase-2.md`: milestones A1 to A6 (alpha) and R1 to R5 (first full release); owning documents 00, 02, 04, 05, 06 and 07 updated, unbuilt items marked planned. Judgement calls LOG 328 to 346, unreviewed |
| Next-phase plan review | Independent review done 2026-11-21: no blocking findings; its fourteen findings fixed in the plan and LOG |
| Product owner answers to `docs/12` section 7 | Done 2026-11-21: all eight answered (NP10 to NP12); question 4 overridden (LOG 341). LOG 330, 333, 341 and 342 decided; LOG 328, 329, 331, 332 and 334 to 346 still unreviewed |
| Push the `docs/12` branch, once the product owner approves the push; then start A1 | Done 2026-11-21 |
| A1 (control gaps) | Done 2026-11-21: one cap function for locked merge floors and races at run start and on patches, `merge.require_tested`, the `<level>.merge_action` decision with a code-host merge before the answer accepted, `notify.send` removed, an unservable action raising an intervention, and toolset settings, the `scripted` runtime outside development and evidence types on `artifact.import` refused. Results in `docs/build/a1-results.md`; LOG 347 to 355, unreviewed (LOG 351 flagged for the product owner) |
| A2 (engine simplification) | Done 2026-11-21: the action stand-in deleted and dry run served by the memory and recording providers on the real paths; one action descriptor registry with an input schema for every action (`run_gate` refused at load); one routing function for every human wait, with agent questions held by the core and `Decision` renamed `Gate`; overrides routed by the core; the shell's logic and state moved into the interpreter core, held there by an architecture test; shared rules, compatibility leftovers and the test root helper consolidated. Results in `docs/build/a2-results.md`; LOG 357 to 387, unreviewed (LOG 362 and 370 flagged for the product owner) |
| A3 (oversight API) | Done 2026-11-21: typed request and response records shared by the API and the CLI, with a generated and checked OpenAPI document; a required reason on answers and aborts, carried with retry causes and summaries to the next agent in a run context; prompts and transcripts indexed, a run's artifacts listed, and each gate linked to what its step consumed; `GET /attention`, `GET /me` and `can_answer` from one answer rule; history on the run page; server-sent change notifications over a `changes` table; templates read; each plan revision recorded and the current one served as data, with `ff run override --change name=@file`; reminders on open waits. Results in `docs/build/a3-results.md`; LOG 388 to 436, unreviewed (LOG 391, 400, 401 and 434 flagged for the product owner) |
| A4 (console first slice) | Built 2026-11-21, **awaiting the product owner's live run** (`docs/build/a4-results.md`, "Live run"): a TypeScript console (Preact, Vite) built by `dotnet build`, embedded in the API host as hashed assets with a Trusted Types policy, replacing the M7 page; client types generated from the OpenAPI document; Node in CI and the host image; `api.auth.mode: none` on loopback only, refusing cross-site requests, or the pasted token; Needs me, the run page (open and closed waits, history, children, timeline, current plan), the decision form with a required reason and every recovery, steering, artifacts and transcripts, abort, start a run; live updates over `/events`; Playwright browser tests in the default category against the API over local stores and a scripted engine. Results in `docs/build/a4-results.md`; LOG 452 to 471, unreviewed (LOG 453, 461 and 470 flagged for the product owner) |
| A5 (sandboxes and runtimes) | Done 2026-11-21: sandboxes made from the repository's `environment.image`, bounded by the sandbox's `allowed_images`, with the base sandbox image's contract in `docs/06`; an agent runtime base class with hooks read by the step's runtime, a runtime contract suite both built-in runtimes pass, and `model_access` per runtime instance; `engine_tools` and `api.base_url` removed and refused; a configuration fault's hint supplied by the failing provider. Results in `docs/build/a5-results.md`; LOG 440 to 451, unreviewed (LOG 441, 442 and 444 flagged for the product owner) |
| A6 (documents match the code) | Done 2026-11-22: every owning document read against the code by four audits; about 120 mismatches fixed, each unbuilt mechanism marked planned with its milestone or removed and listed as a follow-up; library `inputs` removed from `docs/03` (LOG 434); `artifact.import`'s unread `record` input dropped and its needs corrected. Results in `docs/build/a6-results.md`; LOG 472 to 486, unreviewed (LOG 472, 473, 474 and 483 flagged for the product owner). **Alpha is complete** apart from the product owner's A4 live run |
| Alpha follow-ups (A6 follow-ups 1, 2, 8, 9, 10; A4 follow-ups 2, 4, 5; the merge-action CI race) | Done 2026-11-22: keys that loaded and did nothing refused (library `inputs`, `resources`, `notifications`, version constraints, a role's `requires`, a repository's `library`; check 20 retired; settles LOG 472, 473); step-start check 31 in the core for every action, `run.child` and `plan.expand` included (settles LOG 483); `wait.condition`'s needs from its condition; descriptor outputs for `repo.inspect`, `tracker.update`, `delivery.report`; the merge-action race fixed (a cancelled activity's result discarded); `GET /runs/{id}/plan` 204 before the first revision, host start-up refusals one line and exit 2, `POST /runs` checking required parameters. Results in `docs/build/alpha-follow-ups-results.md`; LOG 487 to 495, unreviewed |
| R1 (extension points) | **Next** |

## 7. How to resume

1. Read this file, then `docs/00-principles.md`.
2. Read the latest file in `docs/reviews/`, if any, and apply it.
3. Continue from the first step in section 6 that is not done. Update section 6
   when a step finishes.
4. For spikes, follow `CONTRIBUTING.md` ("Spikes", "Verify, do not assert",
   local setup). The earlier spikes' code is in `spikes/01` to `spikes/03`;
   reuse it where it fits. Write each spike's plan and pre-registered checks
   before running it, and its results as a new document, for example
   `docs/spikes/sp1-results.md`. A result that changes the design updates the
   owning document and adds a LOG entry, marked unreviewed.
