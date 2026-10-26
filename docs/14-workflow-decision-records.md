# Decision records: L1-L12

Status: second draft. Date: 2026-10-27. Deliverable 7 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** L1-L3 are confirmed as the product owner proposed. L6, L9
and L12 are now designed, not asserted: `docs/09` sections 10, 13 and 14. Every
record states where it is applied.

| # | Question | Decision | Source |
|---|---|---|---|
| L1 | One schema or two | One schema. A template is a plan with parameters | Product owner, pre-agreed; confirmed |
| L2 | How much logic | Routing on outcomes, plus CEL. No general scripting | Product owner, pre-agreed; confirmed |
| L3 | Patch format | Domain operations, not JSON Patch | Product owner, pre-agreed; confirmed |
| L4 | Roles | Library content; `write_access` is engine mechanism; runtime by role mapping | Review 09 D4, C8 |
| L5 | Tasks and skills | One reference format; rendered by CEL; rendered prompt content-addressed | Review 09 D5 |
| L6 | Artifact contracts | Declared types with JSON Schema; producer-side validation with field-level errors; consumer compatibility at load | Review 09 D1 |
| L7 | Packaging | NuGet for packages, directory overlay for organisation content; one manifest; lock at run start | Review 09 D8 |
| L8 | Mid-run override | A separate audited operation, not a plan patch; future steps only | Review 09 E5 |
| L9 | Child plans | A patch adding `run.child` steps, from a ticket-list artifact. Not dynamic binding | Review 09 D2 |
| L10 | Validation tiers | Four phases; anything that can change routing or execution blocks | Review 09 F1-F3 |
| L11 | Testing | Temporal replay for interpreter changes; recorded providers re-drive a changed template | Review 09 G1, G2 |
| L12 | Policy references | A gate names a decision type; the policy model resolves who may approve | Review 09 D3, C7 |

---

## L1. Template and plan: one schema or two?

**Options.** (a) One schema: a template is a plan with parameters. (b) Two levels:
a template generates a plan.

**Decision: (a) one schema.**

Reasons:

- One thing to learn, one thing to validate, one thing to patch.
- A re-plan and a template are the same document, so `applyPatch` has one input
  type.
- An agent authoring a patch works in the same form it read (D7).
- No compilation step, so no generated artefact to keep in step with its source.

Cost: a plan carries `parameters` it no longer needs after binding. Accepted — it
is a few lines, and it makes a plan revision self-describing.

**Applied:** `docs/09` section 2.1, `docs/10` section 2.

---

## L2. How much logic belongs in the language?

**Options.** (a) Routing on outcomes only. (b) Outcome routing plus a small
deterministic expression language. (c) General scripting.

**Decision: (b), and the language is CEL.**

Reasons:

- (a) is too weak: a route sometimes depends on *why* a step failed, and on run
  state. Without expressions that becomes either more step kinds or a hidden
  convention.
- (c) is rejected outright. Workflow code must be deterministic and replayable
  (R2, R12). A general scripting language cannot be statically checked for cost,
  cannot be type-checked at load, and would let workflow content do I/O inside
  workflow code.
- CEL is a specification with conformance tests, is total and side-effect free by
  construction, and has a static type checker — which is what makes the F3
  expression type check possible.

**Implementation: `Cel.NET`** (NuGet `Cel.NET` 2.3.1), a C# port of CEL-Java
tracking `google/cel-spec`, targeting `net8.0` and `net10.0`.

**Condition.** Chosen on evidence, not on use. Spike 1 must confirm the checker
can be restricted to a deterministic function set, that evaluation is
deterministic under replay, and that the cost cap is enforceable. If any fails,
the fallback is a hand-written subset with a published grammar.

**Scope.** Predicates and selection only. No comprehensions (review 09, answer 1).
Four namespaces: `params`, `steps`, `run`, `findings`. **No `manifest`
namespace** — repository facts are read per step and never enter the plan
(review 04 Q19).

**Applied:** `docs/09` section 8.

---

## L3. Patch format: generic or domain operations?

**Options.** (a) JSON Patch. (b) Domain operations.

**Decision: (b) domain operations.**

Reasons:

- An agent writes `add_branch` more reliably than a JSON Pointer path into an
  array whose indices shift.
- `applyPatch` can validate intent, not just shape. "Is this step protected?" is
  answerable for `remove_step`; it is not answerable for a generic `remove` on
  `/steps/3`.
- The audit record reads as a workflow change, not as a JSON diff.
- Ten operations cover every case the brief and the engine design raise.

Cost: a new operation needs a schema change. Accepted — that is a versioned
schema change, and it is the point.

**Applied:** `docs/09` section 15.3, `docs/10` section 3.

---

## L4. How are roles defined, and how do steps refer to them?

**Options.** (a) Roles embedded in each step. (b) Roles are library content,
referenced by name.

**Decision: (b), with `write_access` as engine mechanism and runtime selection by
role mapping.**

| Concern | Owner |
|---|---|
| `write_access` (`none`, `branch`, `repository`) | Engine. Enforced by the sandbox and by the hook endpoint on every tool call (`docs/06` 2.2) |
| `tools`, `capabilities`, `skills` | Library role |
| Which agent runtime and model | Organisation layer, `roleMapping` (`docs/07` 4) |

A role declares `runtime.requires` as **capabilities** (`hooks`, `session_resume`),
never a product name. The first draft hard-coded `claude-code` and
`claude-sonnet`; that is brief lesson 5 — templates referring to
installation-specific names — and it would break any installation shipping a
different runtime.

**What each layer may do to a library role:** the organisation may map it to a
runtime instance and a model, and may **tighten** `write_access`. It may not relax
`write_access` or change `tools`. A repository may do nothing (D9).

**Applied:** `docs/09` section 11, `docs/11` section 5.

---

## L5. How are agent tasks and skills referenced, bound and versioned?

**Decision: one reference format, CEL rendering, content-addressed output.**

| Question | Answer |
|---|---|
| Reference format | `<package>[@<constraint>]:<type>/<name>`. Paths are never referenced |
| How parameters reach a task | CEL, in `{{ }}` delimiters inside the task text. The binding context is `inputs.bindings` plus `params`, `run`, `steps` |
| What is rendered | The whole task document, at step start |
| Where the rendered prompt is stored | The artifact store, content-addressed. The step's `task_ref` at execution points at the rendered artifact |
| What the run records | Both the source (package, version, path, hash) and the rendered hash |

Recording both is what lets an audit answer "what exactly did the agent read"
after the package has moved on, and it is what makes a mid-run override auditable.

**Applied:** `docs/09` section 12, `docs/11` sections 3 and 4.

---

## L6. How do artifact contracts work?

**Decision: declared types with JSON Schema; validated on both sides.**

| Element | Design |
|---|---|
| Declaration | `artifactTypes` in the plan, or in the library. One type, one JSON Schema, one version |
| Producer | A step declares `produces: [{ name, type }]` |
| Consumer | A step declares `consumes: [{ name, type, from }]` |
| Producer-side validation | After the step, against the type's schema. On failure the outcome becomes `failed` and **field-level errors are returned to the agent** for a retry, which counts as an attempt |
| Consumer-side, at load | The referenced producer must declare the same type, or one declaring `compatible_with` it. Blocking |
| Consumer-side, at step start | The artifact exists and still validates. Blocking |

Producer-side validation with field-level errors is the direct answer to brief
lesson 8: agents produced artifacts that failed their contract with unhelpful
errors. `acceptance_criteria[2].verification_refs: required property missing` is
actionable; "invalid artifact" is not.

**Worked example:** the `ticket-plan` type, `docs/09` section 10.4.

**Applied:** `docs/09` section 10, `docs/10` section 2 (`ArtifactType`,
`ArtifactDecl`), `docs/12` checks 15, 16, 28.

---

## L7. Library packaging and versioning

**Decision: NuGet for packages, a directory overlay for organisation content.**

| Concern | Mechanism |
|---|---|
| Distributing a versioned, immutable, content-addressed bundle with dependencies | NuGet (`.nupkg`) |
| An organisation adding or overriding content without a packaging pipeline | A directory in the distribution repository, built into the worker image |

They are different problems. The engine is .NET (Q1) and providers are already
NuGet (Q7), so packages get one toolchain, one feed and one versioning scheme.
But an organisation editing a prompt should not need a build pipeline, so the
overlay is loaded as if it were a package named `org/<org>` at `0.0.0-local`.

Rejected: a bespoke archive format (it would reimplement versioning, integrity
and dependency resolution); Git as a distribution mechanism (`docs/01` 2.1: Git is
not a store).

**One manifest**, `kind: LibraryPackage`, holding all seven content types. Tasks
and checks are **not** separate packages (review 09, answer 7): a check and the
task producing its input usually change together.

**One reference format** (section L5). The first draft had three.

**Lock at run start.** Every reference resolves to a concrete package version and
content hash, written to a lock in the artifact store. Without it, a package
updated mid-run changes what a later step resolves, and an audit cannot say what
the agent read.

**Requires.** A package declares `plan_schema_version` and `min_engine_version`,
checked at load (review 09, answer 9).

**Applied:** `docs/11`.

---

## L8. Mid-run library override: semantics and audit

**Decision: a separate audited operation, not a plan patch. Future steps only.**

| Question | Answer |
|---|---|
| Which steps see it | Steps that **start after** the override. A step in flight keeps its resolved content |
| Does it change the plan | No. The plan revision is unchanged |
| Does it change the lock | Yes, with old and new hash |
| Audit | Actor, reason, timestamp, old hash, new hash, `applies_from_step` |
| May it use a newer package version | Yes, if it is in the distribution repository (review 09, answer 8) |

Why not a patch operation: a patch changes structure and produces a revision; an
override changes content resolution and produces a lock entry. Mixing them would
make a plan revision mean two things and would put prompt text into the revision
history. The first draft had both; `override_library_reference` is removed.

This is the prototype's "mid-run library fixes were essential" lesson as a
first-class capability.

**Applied:** `docs/11` section 6, `docs/09` section 15.6.

---

## L9. How is a child plan produced for each ticket of an epic?

**Options.** (a) A planner generates child plans. (b) Children are instantiated
from a template. (c) Both.

**Decision: (c) both, and the mechanism is a patch.**

1. A planning step produces a **ticket-list artifact** (a typed artifact, L6).
2. `plan.replan` consumes it and returns a **patch** adding one `run.child` per
   ticket to a `parallel` step, each with `plan_ref` and bound parameters.
3. `applyPatch` validates it. The branches are now ordinary steps.

**Rejected: dynamic binding** — a `parallel` whose branches are bound directly
from the artifact at execution time.

| | Patch adding `run.child` | Dynamic binding |
|---|---|---|
| Validated before execution | Yes | No; the branches do not exist at load |
| In the readable form | Yes | No |
| Auditable as a diff | Yes | No |
| Deterministic under replay | Yes | Depends on artifact content read in an activity |

Dynamic binding would make the plan unvalidatable at load, breaking F3 and the
whole of `docs/12` section 2.

**How a child re-plan reaches the parent:** the child returns `replan_parent` with
a patch for the parent plan; the parent applies it through the same `applyPatch`
(`docs/01` 3.4).

**Applied:** `docs/09` section 13, `docs/13` sections 2.1 and 3.

---

## L10. Validation tiers

**Decision: four phases, and one blocking rule.**

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

| Phase | Blocks on |
|---|---|
| Template load | Schema, undeclared references, unknown kinds, duplicate ids, expression syntax and types, contract compatibility, unrouted outcomes, cycles without an exit, unreachable steps |
| Binding | Unbound required parameters, library resolution, lock creation |
| Run start | Capability negotiation, policy eligibility, unknown decision types, attempt ceilings |
| Patch | Everything in load, plus protection invariants, `base_revision` staleness, and the operation's authorisation |

**Unbound versus undeclared** (F1): an *undeclared* reference is a load-time
error; an *unbound* required parameter is a binding-time error. A template is
supposed to have unbound parameters, so checking that at load is wrong. The first
draft had this backwards.

**The checks that were missing** (F3): unreachable steps; every outcome the kind
can return is routed; cycles have an exit; producer/consumer contract
compatibility; expression type checks; protected-step reachability on patch.

**No runtime warnings** (review 09, answer 12). Static only.

**Applied:** `docs/12` sections 1 and 2, `docs/09` section 16.

---

## L11. How is a workflow tested without real providers?

**Decision: two separated mechanisms, plus a defined test format.**

| | Temporal replay | Re-driving against recorded providers |
|---|---|---|
| Tests | **Interpreter code** changed | **A template** changed |
| Input | A recorded Temporal history | Recorded provider responses |
| Catches | Non-determinism (R2, R12) | A routing or content regression |
| Where | Engine CI | Library package |

The first draft conflated them. They are not substitutes: replay needs the same
code and the same history; re-driving deliberately changes the template and
produces a new history.

**Test format** (G2): `template`, `parameters`, `provider_script` (step id to
outcome), `expected_path`, `expected_status`, `expected_validation_errors`.

**Where tests live:** with the library package they test (review 09, answer 10).
The engine ships the harness.

**Golden tests:** functional expectations are the norm; golden files are used
**only for the readable form** (review 09, answer 11), because that is the one
output where a diff is genuinely informative.

**Applied:** `docs/12` sections 3 and 4.

---

## L12. How does a gate refer to the policy model?

**Decision: a gate names a decision type. The policy model resolves everything
else.**

| Question | Answered by | Where |
|---|---|---|
| What is being decided | `gate.decision_type` | Template |
| Who may approve, minimum count, whether an agent counts | `policy.approval` | Organisation layer (`docs/07` 4) |
| Whether it may be decided from chat | `policy.chat.allowedDecisions` | Organisation layer |
| Whether it may be auto-approved | `policy.auto_approve`, bounded by floor F1 | Organisation layer |

The first draft put `required_role` and `min_approvers` in the gate's inputs. That
is policy in a template — exactly what L12 exists to prevent — and it would let a
template relax an organisation's approval rule.

`decision_type` values are namespaced (`ticket.merge_approval`,
`epic.delivery_approval`, `delivery.partial_approval`). An unknown decision type
is a blocking error at run start.

**Applied:** `docs/09` section 14, `docs/10` section 2 (`Gate`), `docs/13`
sections 1, 2 and 4.

---

## Decisions taken from review 09 that are not L-questions

| Item | Decision | Applied |
|---|---|---|
| E2 | An agent patch touching protected content becomes a **pending human decision**; a human may approve with a reason. Safety floors stay absolute | `docs/09` 15.5, `docs/13` 3.2 |
| E1 | "Weaken" includes **bypassing** — making a protected step unreachable | `docs/09` 15.2, `docs/12` check 25 |
| E3 | `base_revision` is optimistic concurrency; a stale patch is rejected and re-proposed | `docs/09` 15.1, `docs/10` 3 |
| E6 | Which operations an agent may propose versus a human may apply | `docs/09` 15.4 |
| D9 | A repository may choose a template and **add** skills and checks. It may not override roles, policies or tasks | `docs/11` section 5 |
| D7 | The readable form a planner sees | `docs/12` section 4 |
| A1 | `docs/06` owns step kinds and contracts; this document restates, never redefines | `docs/09` sections 1 and 3 |
| A4 | snake_case everywhere; `docs/01` 3.3 owns step field names | `docs/09` sections 1 and 5 |
| B2 | Terminal directives (`end: completed`) distinct from step ids; step ids must not look like kinds | `docs/09` 6.2, `docs/10` 2 |
| H1-H3 | Worker slot capacity, Temporal-unavailable re-attach, `wait.for_state` poll form | `docs/08` |

---

## Open questions

- **CEL implementation risk** (`docs/09` 18). If Spike 1 finds `Cel.NET` cannot be
  restricted to a deterministic function set, is a hand-written subset with a
  published grammar acceptable?
- **Per-kind input schemas** (`docs/10` 7). Generated from `docs/06`, which would
  need to become machine-readable, or hand-written forty-one times?
- **`run.wait` versus `parallel`** (`docs/13` 5). Is `run.wait` redundant?
- **Artifact type compatibility** (`docs/09` 18). A declared `compatible_with`
  list, or structural compatibility?
