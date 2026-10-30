# Decision records: L1-L12

Status: third draft. Date: 2026-10-31. Deliverable 7 of
`docs/briefs/brief-02-workflow-language.md` (the workflow language brief).

The decisions behind the workflow language, one record per question L1 to L12
from that brief, plus smaller decisions taken in reviews 09 and 11. Each record
says where it is applied. Records are not rewritten when a later decision
changes them; a **Later** line points to what changed.

| # | Question | Decision | Source |
|---|---|---|---|
| L1 | One schema or two | One schema. A template is a plan with parameters | Product owner; confirmed in review 09 |
| L2 | How much logic | Routing on outcomes, plus CEL expressions. No general scripting | Product owner; confirmed in review 09 |
| L3 | Patch format | Domain operations, not JSON Patch | Product owner; confirmed in review 09 |
| L4 | Roles | Library content; `write_access` is engine mechanism; the runtime and model are chosen by role mapping | Review 09 D4, C8 |
| L5 | Tasks and skills | One reference format; rendered with CEL; the rendered prompt stored by content hash | Review 09 D5 |
| L6 | Artifact contracts | Declared types with JSON Schema; the producer's output validated with field-level errors; consumer compatibility checked at load | Review 09 D1 |
| L7 | Packaging | NuGet for packages, a directory overlay for organisation content; one manifest; package versions locked at run start | Review 09 D8 |
| L8 | Mid-run override | A separate audited operation, not a plan patch; affects future steps only | Review 09 E5 |
| L9 | Child plans | A patch adding `run.child` steps, from a ticket-list artifact. Not dynamic binding | Review 09 D2 |
| L10 | Validation tiers | Four phases; anything that can change routing or execution blocks | Review 09 F1-F3 |
| L11 | Testing | Temporal replay for interpreter changes; recorded providers re-drive a changed template | Review 09 G1, G2 |
| L12 | Policy references | A gate names a decision type; the policy model resolves who may approve | Review 09 D3, C7 |

---

## L1. Template and plan: one schema or two?

**Options.** (a) One schema: a template is a plan with parameters. (b) Two levels:
a template generates a plan.

**Decision: (a) one schema.**

- One thing to learn, validate and patch.
- A re-plan and a template are the same document, so `applyPatch` (the single
  path every plan change goes through) has one input type.
- An agent writing a patch works in the same form it read (review 09 D7, the
  readable form).
- No compilation step, so no generated document to keep in step with its source.

Cost: a plan carries `parameters` it no longer needs after binding. Accepted; it
is a few lines, and it makes each plan revision self-describing.

**Applied:** `docs/09` section 2.1, `docs/10` section 2.

---

## L2. How much logic belongs in the language?

**Options.** (a) Routing on outcomes only. (b) Outcome routing plus a small
deterministic expression language. (c) General scripting.

**Decision: (b), and the language is CEL** (Common Expression Language).

- (a) is too weak. A route sometimes depends on *why* a step failed, or on run
  state. Without expressions that becomes more step kinds or a hidden convention.
- (c) is rejected. Workflow code must be deterministic and replayable (risks R2
  and R12 in the `docs/08` risk register: non-determinism in the interpreter). A
  general scripting language cannot be checked for cost or type-checked at load,
  and would let workflow content do I/O inside workflow code.
- CEL is a specification with conformance tests, is total and side-effect free
  by construction, and has a static type checker. That checker is what makes the
  load-time expression type check possible.

**Implementation: `Cel.NET`** (NuGet `Cel.NET` 2.3.1), a C# port of CEL-Java
tracking `google/cel-spec`.

**Condition.** Chosen on evidence, not on use: Spike 1 had to confirm the checker
can be restricted to a deterministic function set, evaluation is deterministic
under replay, and a cost cap is enforceable. The fallback was a hand-written
subset with a published grammar.

**Later:** condition met. Spike 1 passed all four checks, so `Cel.NET` stays and
the fallback is not needed. It also found that `Cel.NET` targets four
frameworks, not the two first stated, and that static cost estimation is the
only cost control, because evaluation cannot be interrupted (`docs/15` section
9.2).

**Scope.** Predicates and selection only. No comprehensions (review 09, answer 1).
Four namespaces: `params`, `steps`, `run`, `findings`. **No `manifest`
namespace**: repository facts are read per step from `feature-flow.yaml` and
never enter the plan (review 04 Q19).

**Applied:** `docs/09` section 8.

---

## L3. Patch format: generic or domain operations?

**Options.** (a) JSON Patch. (b) Domain operations such as `add_branch` and
`remove_step`.

**Decision: (b) domain operations.**

- An agent writes `add_branch` more reliably than a JSON Pointer into an array
  whose indices shift.
- `applyPatch` can validate intent, not just shape. "Is this step protected?" is
  answerable for `remove_step`, not for a generic `remove` on `/steps/3`.
- The audit record reads as a workflow change, not a JSON diff.
- A small fixed set of operations covers every case the brief and the engine
  design raise.

Cost: a new operation needs a schema change. Accepted; that is a versioned
schema change, which is the point.

**Applied:** `docs/09` section 15.3, `docs/10` section 3.

---

## L4. How are roles defined, and how do steps refer to them?

**Options.** (a) Roles embedded in each step. (b) Roles are library content,
referenced by name.

**Decision: (b), with `write_access` as engine mechanism and runtime selection by
role mapping.**

| Concern | Owner |
|---|---|
| `write_access` (`none`, `branch`, `repository`) | The engine. Enforced by the sandbox and by the hook endpoint on every tool call (`docs/06` section 2.2) |
| `tools`, `capabilities`, `skills` | The library role |
| Which agent runtime and model | The organisation layer's `roleMapping` (`docs/07` section 4) |

A role declares the runtime **capabilities** it needs (`hooks`,
`session_resume`), never a product name. Naming a runtime such as `claude-code`
in a template would break any installation shipping a different runtime (the
brief's lesson that templates must not use installation-specific names).

**What each layer may do to a library role:** the organisation may map it to a
runtime and a model, and may **tighten** `write_access`. It may not relax
`write_access` or change `tools`. A repository may change nothing (review 09 D9).

**Later:** `write_access` governs what the agent may change in its own sandbox.
Agents never hold a credential that can push; the engine pushes ticket branches
and performs every merge (DR13, `docs/20`, the merge design). An agent's code
host credential is read-only, per step. The push is part of the agent step, and
an `agent.run` input `push` (default true) can switch it off for a scratch step
(`docs/10` section 4).

**Applied:** `docs/09` section 11, `docs/11` section 5.

---

## L5. How are agent tasks and skills referenced, bound and versioned?

**Decision: one reference format, CEL rendering, content-addressed output.**

| Question | Answer |
|---|---|
| Reference format | `<package>[@<constraint>]:<type>/<name>`. Paths are never referenced |
| How parameters reach a task | CEL, in `{{ }}` delimiters inside the task text. The binding context is `inputs.bindings` plus `params`, `run`, `steps` |
| What is rendered | The whole task document, at step start |
| Where the rendered prompt is stored | The artifact store, addressed by content hash. The step's `task_ref` at execution points at the rendered artifact |
| What the run records | Both the source (package, version, path, hash) and the rendered hash |

Recording both lets an audit answer "what exactly did the agent read" after the
package has moved on, and makes a mid-run override (L8) auditable.

**Applied:** `docs/09` section 12, `docs/11` sections 3 and 4.

---

## L6. How do artifact contracts work?

**Decision: declared types with JSON Schema, validated on both sides.**

| Element | Design |
|---|---|
| Declaration | `artifact_types` in the plan or the library. One type, one JSON Schema, one version |
| Producer | A step declares `produces: [{ name, type }]` |
| Consumer | A step declares `consumes: [{ name, type, from }]` |
| Producer-side validation | After the step, against the type's schema. On failure the outcome is `failed`, and **field-level errors go back to the agent** for a retry, which counts as an attempt |
| Consumer-side, at load | The named producer must declare the same type, or one that declares itself `compatible_with` it. Blocking |
| Consumer-side, at step start | The artifact exists and still validates. Blocking |

Field-level errors answer the brief's lesson that agents produced artifacts that
failed their contract with unhelpful errors.
`acceptance_criteria[2].verification_refs: required property missing` is
actionable; "invalid artifact" is not.

**Worked example:** the `ticket-plan` type, `docs/09` section 10.4.

**Applied:** `docs/09` section 10; `docs/10` section 2 (`ArtifactType`,
`ArtifactDecl`); `docs/12` section 2, checks 15, 16 and 28 (contract
compatibility, producer declares the artifact, consumed artifact validates).

---

## L7. Library packaging and versioning

**Decision: NuGet for packages, a directory overlay for organisation content.**

| Concern | Mechanism |
|---|---|
| Distributing a versioned, immutable, content-addressed bundle with dependencies | NuGet (`.nupkg`) |
| An organisation adding or overriding content without a packaging pipeline | A directory in the distribution repository, built into the worker image |

They are different problems. The engine is .NET (`docs/03` Q1) and providers are
already NuGet packages (`docs/03` Q7), so packages get one toolchain, one feed and
one versioning scheme. But editing a prompt should not need a build pipeline, so
the overlay is loaded as if it were a package named `org/<org>` at
`0.0.0-local`.

Rejected: a bespoke archive format, which would reimplement versioning,
integrity and dependency resolution; and Git as a distribution mechanism (`docs/01`
section 2.1: Git is not a store).

**One manifest**, `kind: LibraryPackage`, holding all seven content types. Tasks
and checks are **not** separate packages (review 09, answer 7): a check and the
task producing its input usually change together.

**One reference format** (L5).

**Package versions locked at run start.** Every reference resolves to a concrete
package version and content hash, recorded in a lock file in the artifact store.
Without it, a package updated mid-run changes what a later step resolves, and an
audit cannot say what the agent read. (This lock file is unrelated to merging.)

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
| Does it change the package lock | Yes, recording the old and new hash |
| Audit | Actor, reason, timestamp, old hash, new hash, `applies_from_step` |
| May it use a newer package version | Yes, if it is in the distribution repository (review 09, answer 8) |

Why not a patch operation: a patch changes structure and produces a plan
revision; an override changes content resolution and produces a lock entry.
Mixing them would make a plan revision mean two things and put prompt text into
the revision history. There is no `override_library_reference` patch operation.

This makes the prototype's lesson, that mid-run library fixes were essential, a
first-class capability.

**Applied:** `docs/11` section 6, `docs/09` section 15.6.

---

## L9. How is a child plan produced for each ticket of an epic?

**Options.** (a) A planner generates child plans. (b) Children are instantiated
from a template. (c) Both.

**Decision: (c) both, and the mechanism is a patch.**

1. A planning step produces a **ticket-list artifact** (a typed artifact, L6).
2. `plan.replan` consumes it and returns a **patch** adding one `run.child` (a
   child run) per ticket to a `parallel` step, each with `plan_ref` and bound
   parameters.
3. `applyPatch` validates it. The branches are now ordinary steps.

**Rejected: dynamic binding**, a `parallel` whose branches are bound from the
artifact at execution time.

| | Patch adding `run.child` | Dynamic binding |
|---|---|---|
| Validated before execution | Yes | No; the branches do not exist at load |
| In the readable form | Yes | No |
| Auditable as a diff | Yes | No |
| Deterministic under replay | Yes | Depends on artifact content read in an activity |

Dynamic binding would make the plan impossible to validate at load, defeating the
validation table (`docs/12` section 2).

**How a child re-plan reaches the parent:** the child returns `replan_parent` with
a patch for the parent plan, and the parent applies it through the same
`applyPatch` (`docs/01` section 3.4: re-planning and steering share one path).

**Later:** a `project` scope sits above epics. A project run starts epic runs
the same way an epic run starts ticket runs (`docs/13` section 8).

**Applied:** `docs/09` section 13, `docs/13` sections 2.1 and 3.

---

## L10. Validation tiers

**Decision: four phases, and one blocking rule.**

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

| Phase | Blocks on |
|---|---|
| Template load | Schema, undeclared references, unknown kinds, duplicate ids, expression syntax and types, contract compatibility, unrouted outcomes, cycles without an exit, unreachable steps |
| Binding | Unbound required parameters, library resolution, package lock creation |
| Run start | Capability negotiation, policy eligibility, unknown decision types, attempt ceilings |
| Patch | Everything in load, plus protection invariants, `base_revision` staleness, and the operation's authorisation |

**Unbound versus undeclared** (review 09 finding F1): an *undeclared* reference
is a load-time error; an *unbound* required parameter is a binding-time error. A
template is supposed to have unbound parameters, so checking for them at load
would be wrong.

**Checks added** (review 09 finding F3): unreachable steps; every outcome the
kind can return is routed; every cycle has an exit; producer and consumer
contract compatibility; expression type checks; a patch must not make a protected
step unreachable.

**No runtime warnings** (review 09, answer 12). Validation is static only.

**Later:** plan validation also checks each merge step's `engine_can_merge`
against floor F1's setting (whether the engine may merge into the default
branch); the engine checks it again at merge time.

**Applied:** `docs/12` sections 1 and 2, `docs/09` section 16.

---

## L11. How is a workflow tested without real providers?

**Decision: two separate mechanisms, plus a defined test format.**

| | Temporal replay | Re-driving against recorded providers |
|---|---|---|
| Tests a change to | **Interpreter code** | **A template** |
| Input | A recorded Temporal history | Recorded provider responses |
| Catches | Non-determinism (risks R2 and R12) | A routing or content regression |
| Where | Engine CI | The library package |

They are not substitutes: replay needs the same code and the same history;
re-driving deliberately changes the template and produces a new history.

**Test format** (review 09 G2): `template`, `parameters`, `provider_script` (step
id to outcome), `expected_path`, `expected_status`, `expected_validation_errors`.

**Where tests live:** with the library package they test (review 09, answer 10).
The engine ships the harness.

**Golden tests:** functional expectations are the norm. Golden files are used
**only for the readable form** (review 09, answer 11), the one output where a
diff is genuinely informative.

**Later:** Spike 1 committed nine recorded histories as the first replay suite
(`docs/15` section 7). Interpreter changes are versioned for replay with
Temporal's `Workflow.Patched` (DR7).

**Applied:** `docs/12` sections 3 and 4.

---

## L12. How does a gate refer to the policy model?

**Decision: a gate names a decision type. The policy model resolves everything
else.**

| Question | Answered by | Where |
|---|---|---|
| What is being decided | `gate.decision_type` | Template |
| Who may approve, minimum count, whether an agent counts | `policy.approval` | Organisation layer (`docs/07` section 4) |
| Whether it may be decided from chat | `policy.chat.allowedDecisions` | Organisation layer |
| Whether it may be approved automatically | `policy.auto_approve`, bounded by floor F1 (whether the engine may merge into the default branch) | Organisation layer |

Putting `required_role` or `min_approvers` in a gate's inputs would be policy in a
template, and would let a template relax an organisation's approval rule.

`decision_type` values are namespaced (`ticket.merge_approval`,
`epic.delivery_approval`, `delivery.partial_approval`). An unknown decision type
is a blocking error at run start.

**Later:** floor F1 is enforced by the engine at merge time, not only by plan
routing (DR2). F1 is now a configurable setting with the same safe default: the
engine may not merge into the default branch. It is set by the organisation,
then the repository's configuration read from its default branch, then the
workflow; the organisation can lock it, and no plan patch or anything an agent
can edit may change it. A merge step states it with `engine_can_merge`.

**Applied:** `docs/09` section 14; `docs/10` section 2 (`Gate`); `docs/13`
sections 1, 2 and 4.

---

## Decisions taken from review 09 that are not L-questions

| Item | Decision | Applied |
|---|---|---|
| E2 | An agent patch touching protected content becomes a **pending human decision**; a human may approve it with a reason. Safety floors stay absolute | `docs/09` section 15.5, `docs/13` section 3.2 |
| E1 | "Weaken" includes **bypassing**: making a protected step unreachable | `docs/09` section 15.2; `docs/12` section 2, check 25 (protected step not removed, weakened or made unreachable) |
| E3 | `base_revision` is optimistic concurrency: a patch written against an old plan revision is rejected and re-proposed | `docs/09` section 15.1, `docs/10` section 3 |
| E6 | Which operations an agent may propose and which a human may apply | `docs/09` section 15.4 |
| D9 | A repository may choose a template and **add** skills and checks. It may not override roles, policies or tasks | `docs/11` section 5 |
| D7 | The readable form a planner sees | `docs/12` section 4 |
| A1 | `docs/06` owns step kinds and their contracts; the language documents restate them, never redefine them | `docs/09` sections 1 and 3 |
| A4 | snake_case everywhere in plans; `docs/01` section 3.3 owns step field names | `docs/09` sections 1 and 5 |
| B2 | Terminal directives (`{ end: completed }`) are distinct from step ids; a step id must not look like a step kind | `docs/09` section 6.2, `docs/10` section 2 |
| H1-H3 | Worker slot capacity, re-attaching when Temporal is unavailable, and the polling form of `wait.for_state` | `docs/08`. **Later:** Spike 1 measured slot capacity and Temporal unavailability (`docs/15` sections 6.3 and 3.8) |

---

## Open questions

None open. Earlier questions and their answers:

| Question | Answer |
|---|---|
| CEL fallback: a hand-written subset with a published grammar | **Acceptable** (product owner, 2026-10-27). **Later:** not needed; `Cel.NET` passed Spike 1 (`docs/15` section 9.2) |
| Per-kind input schemas: generated or hand-written | **Generated.** In implementation the catalogue becomes one machine-readable YAML file that `docs/06` and the schemas are both generated from. An implementation task |
| `run.wait` versus `parallel` | **`run.wait` is out of the version 1 language** unless a worked example needs it. Reserved in `docs/06` |
| Artifact type compatibility | Declared with `compatible_with`: explicit, checkable, and an author's decision rather than an accident of shape |

## Decisions taken from review 11

| Item | Decision | Applied |
|---|---|---|
| A1 | A `run.child` branch reports only its terminal outcome and `replan_parent`. The **interpreter** handles `replan_parent`; routing it to an agent `replan` step would discard the child's patch. Siblings keep running | `docs/09` sections 7.0 and 13.3; `docs/13` sections 2.1, 2.2 and 3 |
| A2 | Ordered work is a **chain**, not `max_concurrency: 1`. `mode: expand` gains `shape: chain`, producing `add_chain` operations that link steps by routes | `docs/09` section 15.3.2, `docs/13` section 2.2 |
| A3 | One resume form, `{ resume: true }`, outside `end`. Resuming is not a run status | `docs/09` sections 6.2 and 15.1.1, `docs/10` section 2 |
| A4 | `tools` and `capabilities` removed from `agent.run` inputs; `write_access` is optional and defaults to the role's | `docs/10` section 4 |
| C1 | The `outputs` cap is a starting value; Spike 2 records actual sizes. **Later:** measured; the cap never bound (at most 5 entries and 71 bytes against 16 and 256) and is kept (`docs/16` section 2) | `docs/09` section 18 |
| C2 | Whether `insert_after` with rewiring beats `add_step` plus `change_route` is argued, not measured; a spike records validation failure rates for both. **Later:** measured; a planner model authored all 20 patches in both forms, and the forms did not differ reliably (`docs/16` section 3) | `docs/09` section 18 |

## Judgement calls accepted by the product owner

Decided 2026-10-27 (review 11 section D).

| Call | Decision |
|---|---|
| `insert_after` rewires the target's routes | Accepted, subject to the C2 measurement. **Later:** measured; no reason to change (`docs/16` section 3) |
| `outputs` cap of 16 entries and 256 bytes per value | Accepted as a starting value (C1). **Later:** measured and kept |
| Both epic fix paths shown; the choice is workflow content | Accepted. The engine prefers neither (`docs/13` sections 2.3 and 5) |
| `docs/07` configuration stays camelCase; plans and library content are snake_case | Accepted. The boundary is stated once, in `docs/09` section 1 |
