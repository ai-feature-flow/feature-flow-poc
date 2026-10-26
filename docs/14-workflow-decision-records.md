# Decision records for brief 2

Status: draft. Companion to `docs/09-workflow-language.md` through `docs/13-workflow-examples.md`.

This document records the design decisions for the workflow language brief. It mirrors the review-driven structure used in `docs/03-decision-records.md`.

## L1. One schema or two?

**Options:**
- (a) one schema: template is a plan with parameters
- (b) two schemas: template and plan are different forms

**Decision: (a) one schema.**

Reasoning:
- keeps authoring simple for both humans and LLMs
- makes a re-plan and a template share the same structure
- reduces validation complexity and the risk of mismatched semantics
- aligns with the product-owner proposal and the engine's data-first design

Applied in: `docs/09-workflow-language.md` section 4.

## L2. How much logic belongs in the language?

**Options:**
- (a) routing on outcomes only
- (b) outcome routing plus a small deterministic expression language
- (c) full scripting capability

**Decision: (b) outcome routing plus a small deterministic expression language.**

Reasoning:
- matches the product-owner proposal and the one-schema requirement
- future-proof without permitting arbitrary execution
- easier to validate, review and replay
- keeps workflow policy decisions in data, not code

Applied in: `docs/09-workflow-language.md` section 3.

## L3. Generic patch or domain patch?

**Options:**
- (a) generic JSON Patch
- (b) domain operations such as insert step, replace inputs, branch add/remove, route change

**Decision: (b) domain operations.**

Reasoning:
- easier for agents and humans to produce safely
- more semantically rich than raw JSON Patch
- better validation and audit hooks
- simpler to enforce `protected` and route invariants

Applied in: `docs/09-workflow-language.md` section 7 and `docs/10-workflow-schema.md` section 4.

## L4. How are roles defined and referenced?

**Options:**
- (a) roles embedded directly into every step
- (b) roles are library content and referenced by name

**Decision: (b) roles are library content.**

Reasoning:
- keeps the engine mechanism and workflow policy separate
- makes roles versioned and overrideable
- allows the engine to enforce `write_access` uniformly without workflow authors deciding runtime permissions

Applied in: `docs/09-workflow-language.md` section 8 and `docs/11-library-and-versioning.md` section 5.

## L5. How are tasks and skills bound and versioned?

**Options:**
- (a) inline text in the plan
- (b) module references with versioned package resolution and content hashes

**Decision: (b) versioned references with content hash tracking.**

Reasoning:
- allows exact audit of what a run used
- supports mid-run override without mutating historical content
- aligns with the artifact-store model and distribution repo architecture

Applied in: `docs/11-library-and-versioning.md` section 6 and 8.

## L6. How do artifact contracts work?

**Options:**
- (a) no artifact contract model
- (b) artifact schemas attached to step outputs and step inputs
- (c) implicit consumer assumptions

**Decision: (b) artifact contracts are first-class and validated by producer and consumer.**

Reasoning:
- keeps field-level validation and better error reporting
- reduces “agent produced garbage and failed late” problems
- makes checks and re-runs deterministic and inspectable

Applied in: `docs/09-workflow-language.md` sections 5 and 10, and `docs/12-validation-and-testing.md` section 2.

## L7. Library packaging and versioning

**Options:**
- (a) ad hoc directory of YAML files
- (b) versioned library packages with distribution overlays

**Decision: (b) versioned packages with distribution overlays.**

Reasoning:
- supports enterprise-specific extension without forking the engine
- matches the distribution-repository model from the engine design
- enables clean override semantics and package-level validation

Applied in: `docs/11-library-and-versioning.md`.

## L8. Mid-run library override semantics

**Options:**
- (a) no mid-run override
- (b) allow override, but only for future steps and with audit

**Decision: (b) future-only override with full audit.**

Reasoning:
- meets the prototype requirement for real-time fixes
- keeps historical execution stable
- makes override intent visible and reviewable

Applied in: `docs/09-workflow-language.md` section 9 and `docs/11-library-and-versioning.md` section 7.

## L9. How is a child plan generated for each ticket of an epic?

**Options:**
- (a) fixed manual child plan per ticket
- (b) parent generates child plans from a ticket template
- (c) both, with the template as the default and the planner as the generator when needed

**Decision: (c) both: generate a child plan from a template and allow a planner to produce a custom plan when required.**

Reasoning:
- keeps the common case simple and deterministic
- preserves flexibility for unusual ticket structures
- aligns with the `parallel`/`join` plan model shown in the engine design

Applied in: `docs/13-workflow-examples.md` section 2.

## L10. Validation tiers

**Options:**
- (a) validate only when the run starts
- (b) validate at template load, run start and patch application

**Decision: (b) multi-phase validation.**

Reasoning:
- catches invalid content earlier
- guarantees patch safety before mutation
- preserves a clean separation between structural and runtime validation

Applied in: `docs/12-validation-and-testing.md` section 2.

## L11. How do authors test a workflow without real providers?

**Options:**
- (a) use real providers only
- (b) use fake providers and recorded runs for dry-run and replay testing

**Decision: (b) dry runs, fake providers and replay tests.**

Reasoning:
- supports fast iteration with no external dependency
- prevents the workflow library from being only “works-on-my-machine”
- aligns with the engine's dry-run and replay strategy

Applied in: `docs/12-validation-and-testing.md`.

## L12. Policy references

**Options:**
- (a) embed policy decisions directly into plans
- (b) reference the policy model through named policy objects

**Decision: (b) policy references are resolved externally to the plan.**

Reasoning:
- keeps the workflow portable and organisation-specific behaviour outside the template itself
- matches the architecture's notion of engine mechanism vs workflow policy
- keeps approval and merge-gate logic consistent across environments

Applied in: `docs/11-library-and-versioning.md` section 8 and `docs/09-workflow-language.md` section 8.

## Closing note

The main design choice is that the workflow language is intentionally narrow: the engine defines the runtime semantics, and the library defines the content. That keeps the plan machine-validated, replayable and safe to amend mid-run without turning the workflow into full general-purpose scripting.
