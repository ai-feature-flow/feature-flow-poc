# Design brief 2: workflow definition language and library

Status: issued. Date: 2026-10-27.
Start after review 08 is applied.
Companion to `brief.md` (the engine brief). Read the engine design first: `docs/01`-`06` and `docs/reviews/`.

## 1. What this brief asks for

Design how workflows are defined, packaged, validated, changed mid-run and tested. Output: design documents, a JSON Schema draft, and worked examples. No production code.

In scope:
- The plan schema: the data the interpreter executes (engine design section 3.3).
- Templates: how authors define reusable workflows, and how a run's plan is produced from one.
- The plan patch format used by re-planning and steering (`applyPatch`, section 3.4).
- The library: packaging of templates, agent tasks (prompts), roles, skills and check definitions; versioning; how an organisation adds or overrides content.
- Validation rules, and how a workflow is tested without real providers.

Out of scope:
- Library content itself (the actual prompts, skills and templates for delivering a ticket). A small example set is wanted, not a full library.
- Console UI for editing workflows.

## 2. Lessons from the previous prototype

| What happened | Implication |
|---|---|
| Two different attempt-counting rule sets depending on which features a workflow used | One semantics for every construct. Already decided for attempts (review 03 Q12) |
| Planner agents had to resubmit the whole workflow to change it; reading it back as one-line JSON cost minutes per step | Planners read a readable form and submit small patches. Design the schema and patch format for LLM authors as much as human ones |
| A re-plan silently changed a ratified detail; a protected step held copied values that went stale | Protected steps, and references (not copies) to repository data. A patch that touches ratified content routes to a human |
| Mid-run library fixes were essential (a broken check prompt was fixed and applied to a running run) | Overriding library content for a running run is a first-class, audited operation with clear semantics |
| Templates hard-coded configuration names that had to exist in every installation | Templates refer to capabilities and roles, never to installation-specific names |
| Placeholders left unfilled reached agents as literal text | Unresolved references are a validation error before the run starts |
| Validation distinguished errors from warnings, but warnings about logic were easy to ignore | Define which checks block and which warn, and make the rule explicit |
| Agents produced artifacts that failed their contract with unhelpful errors | Artifact contracts (schemas) are part of the language; failures name the field and rule |

## 3. Decisions already made that constrain this design

| Decision | Source |
|---|---|
| The plan is data; one interpreter workflow executes every run scope | Engine design 3.1 |
| Re-plans and steering produce plan patches, validated by one `applyPatch`; protected steps cannot be removed or weakened | DP2/S2, section 3.4 |
| A re-plan may change future steps only, not completed ones (v1) | DP7 |
| Fan-out and join (`parallel`, `max_concurrency`) | Review 01 F5 |
| Step kinds and contracts | `docs/06-step-catalogue.md` |
| Engine provides mechanism; workflow content provides policy and practice | Review 03 principle |
| Plan schema versioned separately from the interpreter | S14 |
| Repository facts come from `feature-flow.yaml`, read per step, never copied into the plan | Review 03, review 04 Q19 |
| Organisations extend through a distribution repository, not a fork | Review 01 Q6 |
| Several named instances of a provider type may exist (for example two review providers); steps may select one by name | Review 07 Q3 |
| A dry run executes the plan against fake or recording providers; no per-step dry-run declarations | Review 07 Q6 |

## 4. Questions to answer (recommend, do not decide)

The product owner has agreed the proposals for L1, L2 and L3. Treat them as the starting position: confirm them in the design, or bring evidence against them.

| # | Question | Notes |
|---|---|---|
| L1 | Template and plan: one schema (a template is a plan with parameters) or two levels (a template generates a plan)? | Proposed: one schema. A run's initial plan is the template with parameters bound |
| L2 | How much logic belongs in the language? Routing on outcomes only, or also conditions and expressions? | Proposed: routing on outcomes, plus a small, deterministic, side-effect-free expression language for conditions and parameter references (for example CEL). No general scripting |
| L3 | Patch format: generic (JSON Patch) or domain operations (insert step after, replace step inputs, add parallel branch, change route)? | Proposed: domain operations. Easier for agents to write correctly and for `applyPatch` to validate |
| L4 | How are roles defined (tools, write access, model, runtime), and how do steps refer to them? | Roles are library content; the engine enforces `write_access` |
| L5 | How are agent tasks (prompts) and skills referenced, bound to parameters, and versioned? | Content-addressed in the artifact store once resolved, so a run records exactly what it used |
| L6 | How do artifact contracts work: schema per artifact type, produced and consumed by steps, validated where? | Must give agents field-level errors |
| L7 | Library packaging and versioning: what is a library package, how is it versioned, how does a distribution add or override content? | Must fit the distribution-repository model and NuGet-style packaging, or justify another |
| L8 | Mid-run library override: semantics and audit. Which steps see the new content (future only?), and how is it recorded? | Essential capability from the prototype |
| L9 | How is a child plan produced for each ticket of an epic: generated by a planning step, instantiated from a template, or both? | Affects `run.child` inputs and re-plan of a parent |
| L10 | Validation tiers: what is checked at template load, at run start (with providers and `feature-flow.yaml`), and at each patch? Which failures block? | |
| L11 | Testing workflows: dry run with fake providers, recorded runs replayed against changed templates, golden tests for patches? | A workflow author must be able to test a template without real agents. Build on the fake and recording providers used for dry runs (review 07) |
| L12 | Policy references: how a gate refers to the policy model (approval roles, chat-allowed decisions) without embedding policy | |

## 5. Deliverables

1. Language design document: concepts, the plan schema, templates and parameters, routing, parallel, expressions (if any), patches, roles, tasks, skills, artifact contracts.
2. JSON Schema draft for plans and patches.
3. Library packaging and versioning design, including distribution override and mid-run override.
4. Validation rules table: what, when, blocking or warning.
5. Workflow testing approach.
6. Worked examples:
   - A single-ticket workflow: implement, test, review, fix loop, merge.
   - An epic workflow: plan tickets, run them in parallel, epic review, delivery across two repositories in order.
   - A mid-run re-plan patch that adds a ticket in a second repository.
   - A human session step with pre-submit checks.
7. Decision records for L1-L12.

Writing style: plain technical English, concise, tables and lists over prose, no unexplained references.
