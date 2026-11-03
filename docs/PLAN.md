# Design rewrite plan

Status: design complete 2026-11-01. Build milestones M0 to M4 of
`docs/11-build-plan.md` merged by 2026-11-03; the latest handover is
`docs/build/handover-2026-11-03.md`. Started 2026-10-31. This file is the
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
| 11 | `11-build-plan.md` | Version 1 build: code location and layout, build order, milestones, carry-over from spikes |

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
| M5 (source control, testing and the first real ticket) | Done 2026-11-03 on branch `m5`, milestone pull request into `main` awaiting review: the done-when's live run merged a real ticket into a non-default parent branch on the fixture. Results in `docs/build/m5-results.md`; LOG 107 to 146 unreviewed. State and what needs a look in `docs/build/handover-2026-11-03-m5.md` |
| M6 (findings, validators and levels), starting with its must-fix from M5: a wait holds up a run's hand-over (LOG 139) | **Next** |

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
