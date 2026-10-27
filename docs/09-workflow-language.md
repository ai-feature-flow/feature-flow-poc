# Workflow definition language

Status: second draft. Date: 2026-10-27. Deliverable 1 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** This document is a rewrite, not a revision. Every step
kind, outcome, input name and example is taken from `docs/01`, `docs/06` and
`docs/07`. Where this document changes an engine document, section 17 lists the
change.

Companions: `docs/10-workflow-schema.md` (JSON Schema), `docs/11-library-and-versioning.md`
(packaging), `docs/12-validation-and-testing.md` (validation and testing),
`docs/13-workflow-examples.md` (worked examples), `docs/14-workflow-decision-records.md`
(L1-L12 records).

---

## 1. Sources of truth

One fact lives in one place. This is what review 09 A1 asked for.

| Fact | Owner | This document |
|---|---|---|
| The set of step kinds, and each kind's inputs, outcomes, form, scope, idempotency, limits and audit | `docs/06-step-catalogue.md` | Section 3 restates the kind list and the outcome each kind may return. It does not redefine contracts |
| The outcome vocabulary | `docs/06` section 1.1, plus `any_failed` (`docs/01` 3.3.1) and `not_approved` (`docs/06` 3.3) | Section 4 |
| Step field names and naming convention | `docs/01` section 3.3 | Section 5 |
| Join semantics | `docs/01` section 3.3.1 | Section 7 |
| Policy objects and who may approve | `docs/07` sections 4 and 7 | Section 14 |
| Plan schema, patch schema, expression language, artifact contracts, library packaging | **This document and `docs/10`-`docs/13`** | — |

Naming convention: **snake_case everywhere**, matching `docs/06` (`task_ref`,
`write_access`, `max_concurrency`, `context_refs`, `pr_ref`). No camelCase.

**The boundary** (review 10 B7). "Everywhere" was too strong, and `docs/07`
configuration is camelCase throughout. The rule is by document family:

| Family | Convention | Examples |
|---|---|---|
| Plans, patches, library content, artifact schemas | **snake_case** | `task_ref`, `write_access`, `tier_tags`, `base_revision`, `resume_at` |
| Configuration (`docs/07`) | camelCase, unchanged | `roleMapping`, `agentRuntime`, `hardCeiling`, `minCount` |

Configuration keeps camelCase because it is YAML consumed by .NET binding, it is
already written and reviewed across three profiles, and renaming it would touch
every installation file for no functional gain. The two families meet at
`policy.replan.allowed_roles` (section 11.0), which is configuration and so is
camelCase, and at `roleMapping`, which is configuration referencing library
content.

Within the plan and library family, the second draft's camelCase is fixed:
`artifactTypes` becomes `artifact_types`, `schemaVersion` is dropped entirely
(review 10, answer: `apiVersion` already carries the plan schema version).

---

## 2. Concepts

| Concept | Meaning |
|---|---|
| Plan | The data the interpreter executes. One revision at a time lives in workflow state; every revision is in the artifact store |
| Template | A plan with unbound parameters. **One schema** (L1) |
| Plan revision | An immutable, numbered version of a plan. A re-plan produces a new revision |
| Step | A node in the plan: `id`, `kind`, `scope`, `inputs`, `requires`, `limits`, `routes`, `protected`, optional `gate` |
| Outcome | Exactly one result per step execution. The routing vocabulary |
| Route | Outcome to next step id, or to a terminal directive |
| Terminal directive | `end: <status>`. Not a step id |
| Patch | An ordered list of domain operations against a named `base_revision` |
| Library | Versioned workflow content: templates, roles, tasks, skills, checks, artifact types |

### 2.1 One schema (L1)

A template and a plan are the same document. The difference is whether
`parameters` are bound.

- A template declares `parameters` with types, defaults and constraints.
- A run binds them. Binding produces plan revision 1.
- A re-plan produces revision 2, 3, and so on, from the same schema.

There is no second syntactic form, and no template-to-plan compilation step.

---

## 3. Step kinds

Forty-one kinds, generated from `docs/06`. This list is a restatement; `docs/06`
owns the contracts.

| Family | Kinds |
|---|---|
| Agent | `agent.run` |
| Human | `human.gate`, `human.session`, `policy.auto_approve` |
| Composition | `run.child`, `parallel` |
| Planning | `plan.replan`, `plan.validate` |
| Source control | `scm.create_branch`, `scm.merge_base_forward`, `scm.open_pr`, `scm.update_pr`, `scm.merge`, `scm.revert` |
| CI | `ci.trigger`, `ci.wait_checks`, `ci.read_results` |
| Testing | `test.run_tiers`, `test.run_composition` |
| Analysis | `analysis.run` |
| Review | `review.run` |
| Findings | `findings.triage`, `findings.post_declines`, `findings.evaluate_readiness` |
| Fix verification | `fix.verify` |
| Waiting | `wait.for_state` |
| Artifacts | `artifact.bundle`, `artifact.validate`, `artifact.import`, `artifact.publish` |
| Delivery | `delivery.deploy_order`, `delivery.deploy`, `delivery.gate`, `delivery.report` |
| Tracker | `tracker.create`, `tracker.update`, `tracker.transition`, `tracker.link`, `tracker.comment` |
| Notifications | `notify.send` |

Steering is not a step. It is a control-plane capability that produces a patch or
an attempt grant (`docs/01` section 7).

**`run.wait` is not in the version 1 language** (review 10, answer). It was in the
second draft's enum but unused in every worked example — `parallel` with
`run.child` branches covers every case the examples raise. It is removed from the
schema enum and marked **reserved** in `docs/06`. If a worked example later needs
it, it comes back with that example.

---

## 4. Outcomes

### 4.1 Vocabulary

From `docs/06` section 1.1, plus `any_failed` and `not_approved`.

| Outcome | Meaning |
|---|---|
| `completed` | The step did its work |
| `blocked` | The step cannot proceed; something external must change |
| `failed` | The step did its work and the work was wrong or rejected |
| `attempts_exhausted` | The allowance is spent. Never a silent failure (`docs/01` 3.7) |
| `replan_requested` | The step produced a plan patch |
| `replan_parent` | A child run asks its parent to re-plan |
| `partial` | Some of the work happened (delivery) |
| `approved` | A human or policy approval |
| `rejected` | A human rejection |
| `not_approved` | `policy.auto_approve` did not approve; a human gate follows |
| `findings_open` | Blocking findings remain |
| `ready` | The merge bar is met (D8) |
| `any_failed` | Aggregate outcome of `parallel` and `run.wait` when the join is not satisfied |

### 4.2 Which outcomes each kind may return

Every step must route every outcome its kind can return, or the template fails
validation (section 6.3). `attempts_exhausted` is produced by the interpreter for
any step whose allowance is spent (`docs/01` 3.7), so **every** step must route
it.

| Kind | Outcomes |
|---|---|
| `agent.run` | `completed`, `blocked`, `failed`, `attempts_exhausted`, `replan_requested` |
| `human.gate` | `approved`, `rejected` |
| `human.session` | `completed`, `rejected` |
| `policy.auto_approve` | `approved`, `not_approved` |
| `run.child` | `completed`, `failed`, `replan_parent`, `attempts_exhausted` |
| `parallel` | `completed`, `any_failed` |
| `plan.replan` | `replan_requested`, `failed`, `attempts_exhausted` |
| `plan.validate` | `completed`, `failed` |
| `scm.create_branch` | `completed`, `failed`, `attempts_exhausted` |
| `scm.merge_base_forward` | `completed`, `blocked`, `attempts_exhausted` |
| `scm.open_pr` | `completed`, `failed`, `attempts_exhausted` |
| `scm.update_pr` | `completed`, `failed`, `attempts_exhausted` |
| `scm.merge` | `completed`, `blocked`, `rejected`, `attempts_exhausted` |
| `scm.revert` | `completed`, `failed`, `attempts_exhausted` |
| `ci.trigger` | `completed`, `failed`, `attempts_exhausted` |
| `ci.wait_checks` | `completed`, `failed`, `blocked`, `attempts_exhausted` |
| `ci.read_results` | `completed`, `partial`, `attempts_exhausted` |
| `test.run_tiers` | `completed`, `failed`, `attempts_exhausted` |
| `test.run_composition` | `completed`, `failed`, `attempts_exhausted` |
| `analysis.run` | `completed`, `failed`, `attempts_exhausted` |
| `review.run` | `completed`, `findings_open`, `failed`, `attempts_exhausted` |
| `findings.triage` | `completed`, `blocked`, `attempts_exhausted` |
| `findings.post_declines` | `completed`, `failed`, `attempts_exhausted` |
| `findings.evaluate_readiness` | `ready`, `findings_open` |
| `fix.verify` | `completed`, `failed`, `attempts_exhausted` |
| `wait.for_state` | `completed`, `blocked`, `attempts_exhausted` |
| `artifact.bundle` | `completed`, `failed`, `attempts_exhausted` |
| `artifact.validate` | `completed`, `failed` |
| `artifact.import` | `completed`, `failed`, `attempts_exhausted` |
| `artifact.publish` | `completed`, `failed`, `attempts_exhausted` |
| `delivery.deploy_order` | `completed` |
| `delivery.deploy` | `completed`, `failed`, `blocked`, `attempts_exhausted` |
| `delivery.gate` | `approved`, `rejected` |
| `delivery.report` | `completed`, `attempts_exhausted` |
| `tracker.create` | `completed`, `failed`, `attempts_exhausted` |
| `tracker.update` | `completed`, `failed`, `attempts_exhausted` |
| `tracker.transition` | `completed`, `blocked`, `attempts_exhausted` |
| `tracker.link` | `completed`, `failed`, `attempts_exhausted` |
| `tracker.comment` | `completed`, `failed`, `attempts_exhausted` |
| `notify.send` | `completed`, `failed`, `attempts_exhausted` |

Local, pure steps (`plan.validate`, `policy.auto_approve`,
`findings.evaluate_readiness`, `delivery.deploy_order`, `artifact.validate`) do
not consume attempts, so they do not return `attempts_exhausted`.

---

## 5. Step fields

From `docs/01` section 3.3. One field set, snake_case.

```yaml
- id: implement
  kind: agent.run
  scope: ticket
  protected: true
  inputs:
    role: feature-flow-default:roles/implementer
    task_ref: feature-flow-default:tasks/implement-ticket
    write_access: branch
  requires:
    capabilities: [sandbox, agent_runtime]
  limits:
    attempts: 3
    start_to_close: 4h
    heartbeat: 30s
  routes:
    completed: run_tests
    blocked: replan
    failed: replan
    attempts_exhausted: escalate
  gate:
    decision_type: ticket.implement_review
```

| Field | Required | Notes |
|---|---|---|
| `id` | Yes | Unique in the plan. **Must not look like a step kind** (review 09 B2): `run_tests`, not `test.run_tiers` |
| `kind` | Yes | From section 3 |
| `scope` | No | `ticket`, `epic`, `delivery`, `any`. Defaults to the plan's scope |
| `inputs` | No | Per kind, from `docs/06`. Artifact references, never inline content |
| `requires` | No | Provider capabilities. Checked at run start (S3) and again if `feature-flow.yaml` changes |
| `limits` | No | `attempts`, `start_to_close`, `heartbeat`. Within policy ceilings (F4) |
| `routes` | Yes | Outcome to step id or terminal directive. Section 6 |
| `protected` | No | Cannot be removed or weakened (section 15.2) |
| `gate` | No | A decision type, not policy content. Section 14 |
| `produces` / `consumes` | No | Artifact contracts. Section 10 |

---

## 6. Routing

### 6.1 One form: per-step `routes`

Review 09 B1. There is no top-level `routes` list. Each step owns its routes.

```yaml
routes:
  completed: run_tests
  failed:
    - when: "findings.blocking_count > 0"
      to: triage_findings
    - when: "steps.implement.outcome == 'blocked'"
      to: escalate
    - to: replan              # required unguarded fallback
```

Rules:

- A route value is either a **step id**, a **terminal directive**, or an **ordered
  list of guarded routes**.
- Guards are evaluated in order; the first true guard wins.
- A guarded list **must** end with an unguarded fallback. Validation enforces it.
- An unrouted outcome that the kind can return is a **blocking** validation error.

### 6.2 Terminal directives

Review 09 B2; review 10 B9. A route target is a step id or a directive, never
both.

**Directives are a mapping, not a string.** The second draft used
`completed: "end: completed"`, which needed quoting to survive YAML — an
unquoted colon-space is not a valid scalar — and which the schema could only
check with a regex.

```yaml
routes:
  completed: { end: completed }
  rejected: { end: rejected }
```

| Directive | Run status |
|---|---|
| `{ end: completed }` | The run succeeded |
| `{ end: failed }` | The run failed |
| `{ end: partial }` | Delivery: some repositories delivered (`docs/01` 7.3) |
| `{ end: rejected }` | A human rejected and the plan has nowhere else to go |

Typed in the schema as an object with one required `end` property, so no regex
and no quoting. Step ids must not look like kinds, so a directive is never
ambiguous with a step id.

**`resume` is not an `end` value** (review 11 A3). `end` is a **run status**, and
resuming is not one — the run has not ended. The third draft had both
`{ end: resume }` and `{ resume: true }` in different places. One form, outside
`end`:

```yaml
routes:
  replan_requested: { resume: true }
```

`{ resume: true }` is a route target in its own right, valid only on a
`plan.replan` step, and it resolves to the patch's `resume_at` (section 15.1.1).
`resume` is removed from the `end` enum in `docs/10`.

### 6.3 Loops are bounded by the engine

Review 09 B3. A plan may contain a cycle — test, fix, retest is the normal case —
but the plan does not count iterations.

| Bound | Mechanism |
|---|---|
| Per-step allowance | `limits.attempts`, counted by `docs/01` 3.7. Every entry counts |
| Per-run ceiling | `policy.attempts.ceiling` (`docs/07` 4) |
| Findings loop | Governed by the findings ledger, not a count. A finding that reappears after being fixed twice **escalates** instead of looping (`docs/06` 9.1). A declined finding never restarts the loop |
| Exhaustion | Surfaces as `attempts_exhausted`, routed by the plan to a re-plan or a gate. Never a silent failure |

Validation requires **every cycle to have a route out**: at least one step in the
cycle must route an outcome to a step outside the cycle or to a terminal
directive. A cycle with no exit is a blocking error (section 16.3).

---

## 7. Parallel and joins

From `docs/01` 3.3.1.

```yaml
- id: tickets
  kind: parallel
  inputs:
    branches: [ticket_a, ticket_b, ticket_c]
    join: all                 # all | any | n
    max_concurrency: 2
  routes:
    completed: epic_review
    any_failed: escalate
```

| Join | Semantics |
|---|---|
| `all` | Waits for every branch |
| `any` | Completes when the first branch reaches a **terminal outcome**, and **cancels the rest** |
| `n` | Completes when `n` branches succeed |

`any_failed` is the aggregate route when a branch fails and the join policy is
not satisfied.

### 7.0 What a branch reports, and what it may route to

Review 10 A2; review 11 A1. Directives are **run** statuses (section 6.2), so a
branch routing `completed: "end: completed"` would end the whole run when one
ticket finished. The second draft's examples did exactly that, and the third
draft then routed branch outcomes to **parent** steps, which left the join and
the sibling branches undefined while that ran.

> **A `run.child` branch reports only its terminal outcome and `replan_parent` to
> the parent. It routes nothing else. `blocked` is handled inside the child's own
> plan.**

| Branch outcome | Who handles it | Effect on the join |
|---|---|---|
| `completed` | The join | Counts towards `all`, `any` or `n` |
| `failed` | The join | Counts towards `any_failed` |
| `attempts_exhausted` | The join | Counts towards `any_failed` |
| `replan_parent` | **The interpreter** | The child's patch goes through `applyPatch` with the E2 rules. **Siblings keep running.** The branch **counts as completed for the join** — see below |
| `blocked` | The child's own plan | Never reaches the parent |

**`replan_parent` counts as completed for the join** (review 12 B5a). The child
did its work and reported a discovery; that is not a failure, and leaving the
branch unresolved would deadlock a `join: all`. The parent applies the patch and
the join proceeds.

**The ticket is not automatically re-run.** Whether the parent's patch re-runs
the child is a decision the patch makes, not a consequence of `replan_parent`. If
the patch inserts a new step or re-points a route so the child's work is
revisited, the child runs again; if it only adds future work, it does not. The
interpreter does not infer a re-run, because inferring it would silently consume
the child's attempt budget.

Two consequences the third draft got wrong:

- **`replan_parent` is handled by the interpreter, not routed to an agent
  `replan` step.** Routing it to `replan` would re-plan from
  `steps.plan_tickets.outcome` and **ignore the child's patch entirely** — the
  child's discovery would be discarded. The interpreter applies the child's patch
  through the same `applyPatch`, with the same three results: applied, pending
  human decision, rejected (section 15.5).
- **Siblings keep running.** A `replan_parent` from one branch does not suspend
  the others. The patch applies to future steps; in-flight branches are
  untouched, which is already the rule for a re-plan arriving while a branch is
  in flight (`docs/08` 2.1).

A branch therefore has **no `routes` at all** in the common case. It may not use
a directive, and it may not route a terminal outcome. Validation enforces both.

The `parallel` step's own `routes` carry `completed` and `any_failed`, which is
where the run-level decision is made.

### 7.1 A ticket branch is a `run.child`

Review 09 A5. A branch is a step or a sub-plan; a ticket branch is a `run.child`
step carrying `plan_ref`.

```yaml
- id: ticket_a
  kind: run.child
  inputs:
    child_scope: ticket
    plan_ref: feature-flow-default:templates/ticket-delivery
    parameters:
      ticket_id: "EPIC-101"
      repositories: { expr: "params.repositories" }
  # No routes: the branch reports its terminal outcome to the join, and
  # replan_parent is handled by the interpreter.
```

A re-plan may add branches to a `parallel` step that has not started, and may not
remove a protected branch (`docs/01` 3.3.1).

---

## 8. Expressions

### 8.1 The language is CEL

Review 09 D6. The language is the **Common Expression Language**, not a
look-alike.

- **.NET implementation: `Cel.NET`** (NuGet `Cel.NET`, version 2.3.1,
  <https://github.com/rayokota/cel.net>). It is a C# port of CEL-Java, tracks
  `google/cel-spec`, and is a dependency of `Confluent.SchemaRegistry.Rules`.
  **Confirmed by Spike 1, 2026-10-27.** It ships four target frameworks —
  `net10.0`, `net8.0`, `netstandard2.1` and `net462` — not the two this section
  previously stated, and pulls seven transitive dependencies including
  Antlr4.Runtime.Standard, Apache.Avro and Google.Protobuf.
- **Checked, and now confirmed.** Spike 1 ran the four checks and **all four
  pass**: the checker rejects unregistered functions and unknown variables;
  evaluation is deterministic under replay (120,000 evaluations, one distinct
  result each, order-independent); **static cost estimation exists**
  (`Cel.Cel.EstimateCost(object)` / `Cel.Prog.Cost()`); and the cost limit is
  enforceable. Cel.NET stays. The fallback is no longer needed.
- **Cost estimation is load-bearing, not defence in depth.** Cel.NET has no
  runtime interrupt: `DecObserveEval` fires but the exception is swallowed, so an
  expression that is already running cannot be stopped. The static cost estimate
  is therefore the only thing that stops an expensive expression before it runs.
  The length and AST-node caps in section 8.5 remain as a second control.
- **The environment must be built with `Env.NewCustomEnv` and explicit opt-in.**
  `Env.NewEnv()` and the default `ScriptHost` register the whole standard library
  **and comprehensions** before the engine declares anything, so a checker built
  on them is not restricted. This is what makes the first check above true in the
  engine rather than only in the spike.
- **`lower` and `upper` are registered by the engine** (review 12, answer 3).
  Cel.NET does not declare them anywhere — not in the standard library, not in
  `StringsLib` — but section 8.4 allows them. The engine registers them using
  culture-invariant conversion (`ToLowerInvariant`); the culture-sensitive form
  is non-deterministic across machines and is a replay hazard.
- **Maintainer risk** (review 10 C2). Cel.NET has a small maintainer base. It is
  recorded as a risk, and the engine's exposure is limited: CEL is used for route
  guards and input binding, so a forced migration touches the expression
  evaluator, not the interpreter. The fallback remains a hand-written subset with
  a published grammar, **accepted by the product owner, 2026-10-27**, if that
  migration is ever forced.

Why CEL rather than a bespoke subset: it is a specification with conformance
tests, it is side-effect free and total by construction (no loops, no I/O, no
recursion), and it has a static type checker, which is what makes the F3
expression type check possible at load time.

### 8.2 Where expressions appear

| Place | Form |
|---|---|
| A `when` guard in a route | Bare string: `when: "findings.blocking_count > 0"` |
| An input value | Mapping: `repositories: { expr: "params.ticket_a_repos" }` |
| A parameter default or constraint | Bare string in the parameter declaration |
| A task body | `{{ <cel> }}` delimiters. Section 12.2 |

A bare YAML string is a **literal**. An expression is always either the value of a
`when` key or the value of an `expr` key. There is no third form, and **`${...}`
is not part of the language** (review 09 D6).

### 8.3 Variable namespaces

Four namespaces. Nothing else is in scope.

| Namespace | Contents | Example |
|---|---|---|
| `params` | Bound run parameters | `params.ticket_id` |
| `steps.<id>.outcome` | The outcome a completed step returned | `steps.review.outcome == 'findings_open'` |
| `steps.<id>.artifacts.<name>` | An artifact **reference** produced by a step. Never content | `steps.plan_spec.artifacts.plan` |
| `steps.<id>.outputs.<name>` | A small typed value the step published. Section 8.3.2 | `steps.plan_spec.outputs.tier_tags` |
| `steps.<id>.choice` | A recorded choice from a gate (review 10 B12) | `steps.delivery_gate.choice == 'revert'` |
| `run` | Section 8.3.1 | `size(run.repositories) > 1` |
| `findings` | `findings.blocking_count`, `findings.open_count` | `findings.blocking_count == 0` |

**There is no `manifest` namespace.** Repository facts are read per step from
`feature-flow.yaml` and never copied into the plan (review 04 Q19). An expression
that needed manifest data would reintroduce exactly the staleness that decision
removed. The first draft's `facts` and `repo` namespaces are gone; they were
never defined.

#### 8.3.1 The `run` namespace

Review 10 A4. Every field, with its type and where its value comes from.

| Field | Type | Source |
|---|---|---|
| `run.id` | string | The run id, assigned at start |
| `run.scope` | string | `ticket`, `epic`, `delivery`. From the run request |
| `run.repositories` | list of string | The run request. Not the manifest |
| `run.branch` | string | Resolved by the step that needs it, from the branch convention |
| `run.integration_branch` | string | Same |
| `run.head_sha` | string | **A reference resolved by the consuming step**, not a value in workflow state |
| `run.aggregate_diff` | string | Same. An artifact reference |
| `run.tracker_issue` | string | The run request |
| `run.delivery_ledger` | string | An artifact reference, read through an activity (`docs/01` 7.3) |
| `run.delivery_prs` | list of string | PR references for the delivery, from the delivery ledger |

Anything that **changes during the run** — `head_sha`, `aggregate_diff` — is a
reference the consuming step resolves in its activity. It is not a value held in
workflow state, because reading it there would be I/O in workflow code.

`findings` is a **namespace**, not a field of `run`. The second draft's examples
used `run.findings` and `run.findings.open`; both are wrong and are fixed in
`docs/13`.

#### 8.3.2 Expressions never read artifact content

Review 10 A3. The second draft dereferenced artifacts in expressions:
`steps.plan_spec.artifacts.plan.verification.tier_tags`. That reads artifact
**content** inside workflow code, which is I/O and breaks replay (R2, R12).

> **Expressions see references, outcomes and published outputs. Never content.**

Where a step needs a value from an artifact, the **consuming step** reads it in
its own activity. `run_tests` consumes the plan artifact and reads `tier_tags`
itself; the expression only passes the reference.

For the cases where a value genuinely must reach workflow state, a step may
publish a small, typed, size-capped `outputs` map:

```yaml
- id: plan_spec
  kind: agent.run
  produces: [{ name: plan, type: ticket-plan }]
  outputs:
    tier_tags: { type: array, max_size: 16 }
  routes: { ... }
```

| Rule | Value |
|---|---|
| Declared in | The step contract, per kind (`docs/06`) |
| Types | `string`, `number`, `boolean`, and lists of those |
| Cap | 16 entries, 256 bytes per value. Checked at load |
| Written by | The step's activity result, never by workflow code |
| Read as | `steps.<id>.outputs.<name>` |

The cap is what keeps this from becoming a second, looser artifact store inside
workflow state. A step that needs more than 16 small values should be passing an
artifact reference and reading it in an activity.

### 8.4 Allowed functions

Predicates and selection only (review 09, answer 1). No comprehensions.

`size`, `contains`, `all`, `any`, `exists`, `startsWith`, `endsWith`, `matches`,
`lower`, `upper`, plus arithmetic, comparison and boolean operators.

### 8.5 Bounds

| Bound | Value |
|---|---|
| Loops, recursion, I/O, clock, randomness | Impossible: not in CEL, and the engine registers no such function |
| Expression length | 1000 characters, checked at load |
| AST nodes | 200, checked at load |
| Evaluation cost | CEL's static cost estimate, capped; the checker rejects over budget |
| Evaluation time | Bounded by the cost cap. Evaluation is pure workflow code, so it must not delay a heartbeat |

---

## 9. Parameters and binding

```yaml
parameters:
  ticket_id:
    type: string
    required: true
  ticket_a_repos:
    type: array
    default: { expr: "run.repositories" }
```

Binding happens at run start. Review 09 F1 splits the two failure modes:

| Failure | Detected | Blocks |
|---|---|---|
| An **undeclared** reference — `params.foo` where `foo` is not declared | Template load | Yes |
| An **unbound** reference — a required parameter with no value | Binding, at run start | Yes |

A template is *supposed* to have unbound parameters, so "unresolved reference" is
not a load-time error. It is a binding-time error. The first draft had this
backwards.

---

## 10. Artifact contracts (L6)

Review 09 D1. This was absent from the first draft.

### 10.1 Declaring a type

Artifact types are library content. One type, one JSON Schema.

```yaml
artifact_types:
  - name: ticket-plan
    version: 1
    schema_ref: feature-flow-default:schemas/ticket-plan.schema.json
    description: "The plan for one ticket: summary, acceptance criteria, verification requirements"
```

### 10.2 Producing and consuming

A step declares what it produces and what it consumes.

```yaml
- id: plan_spec
  kind: agent.run
  produces:
    - name: plan
      type: ticket-plan
  routes: { completed: design, failed: gate_spec, attempts_exhausted: gate_spec }

- id: design
  kind: agent.run
  consumes:
    - name: plan
      type: ticket-plan
      from: plan_spec
  routes: { completed: implement, blocked: gate_design, attempts_exhausted: gate_design }
```

### 10.3 Where validation happens

| Where | What | On failure |
|---|---|---|
| Producer side, after the step | The produced artifact is validated against the type's JSON Schema | The **field-level errors are returned to the agent in the same session**, for a bounded number of correction turns (default 2). Only if the artifact still fails is the outcome `failed` |
| Consumer side, at load | For each `consumes`, the referenced producer's `produces` entry must declare the same type, or a type that declares `compatible_with` it | Blocking validation error |
| Consumer side, at step start | The artifact exists and still validates | Blocking; the step fails |

Producer-side validation is what turns "the agent produced something that failed
its contract with an unhelpful error" (brief lesson 8) into a named field and a
named rule.

**Correction turns, not a whole re-run** (review 10 B5). A missing required field
does not need the agent to start again; in the prototype it could usually be
fixed in place. The step returns the field-level errors into the **running
session** and lets the agent correct them, for a bounded number of turns
(`limits.correction_turns`, default 2). Only then does the outcome become
`failed`. A correction turn is **not** an attempt: no new sandbox, no new
session, and the work already done is kept.

### 10.4 Worked example: the ticket plan artifact

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://feature-flow.dev/artifacts/ticket-plan/v1",
  "title": "ticket-plan",
  "type": "object",
  "required": ["ticket_id", "summary", "acceptance_criteria", "verification"],
  "properties": {
    "ticket_id": { "type": "string" },
    "summary": { "type": "string", "minLength": 20 },
    "acceptance_criteria": {
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "required": ["id", "statement", "verification_refs"],
        "properties": {
          "id": { "type": "string", "pattern": "^AC-[0-9]+$" },
          "statement": { "type": "string", "minLength": 10 },
          "verification_refs": { "type": "array", "items": { "type": "string" }, "minItems": 1 }
        }
      }
    },
    "verification": {
      "type": "object",
      "required": ["tier_tags", "notes"],
      "properties": {
        "tier_tags": { "type": "array", "items": { "type": "string" } },
        "notes": { "type": "string" }
      }
    }
  }
}
```

A producer that omits `acceptance_criteria[2].verification_refs` gets:
`artifact ticket-plan: acceptance_criteria[2].verification_refs: required property
missing`. That string goes back to the agent.

---

## 11. Roles (L4)

Review 09 D4 and C8; review 10 A1 and B11. Roles are library content;
`write_access` is engine mechanism (`docs/06` 2.2).

```yaml
roles:
  - name: implementer
    write_access: branch          # none | branch | repository  (docs/06 2.2)
    tools: [read, edit, bash, search]
    capabilities: [os-linux, git]
    runtime:
      requires: [hooks, session_resume]   # a capability, not a product name
    skills: [feature-flow-default:skills/patch-review]
```

This is the **role**, so `tools` and `capabilities` belong here — that is where
section 11.0 says they come from. They are absent from `agent.run` **inputs**,
which is what stops a step or a patch from setting them.

| Field | Meaning |
|---|---|
| `write_access` | `none`, `branch`, `repository`. Enforced by the sandbox and by the hook endpoint on every tool call |
| `tools` | The tool allowlist the agent runtime is given |
| `capabilities` | Queue routing (S1) and capability negotiation (S3) |
| `runtime.requires` | Capabilities the agent runtime must declare. **Not** a product name |
| `skills` | Library references |

### 11.0 Privilege comes from the role, never from the step

Review 10 A1. This closes an escalation path the second draft left open.

`agent.run` inputs carried `role`, `tools`, `write_access` and `capabilities`,
and the role *also* defined `write_access`, `tools` and `capabilities`. An agent
may not `replace_role` (section 15.4), but it **may** `replace_inputs` on any
unprotected step, or `insert_after` a new `agent.run`. Either could set
`write_access: repository` and any tool list. The same setting in two places,
with the weaker one writable by an agent, is a privilege escalation.

The rule:

> **`write_access`, `tools` and `capabilities` come only from the role.** A step
> may **tighten** `write_access`, never relax it. It may not set `tools` or
> `capabilities` at all.

| Setting | Source | A step may |
|---|---|---|
| `write_access` | The role | Tighten: `repository` to `branch`, `branch` to `none`, and so on. Never relax |
| `tools` | The role | Nothing |
| `capabilities` | The role | Nothing |

So a planner step is `role: feature-flow-default:roles/planner` with
`write_access: none` — a tightening, which is allowed and which keeps the floor
visible in the plan
(review 06, answer 1). A step cannot grant itself `repository` access by naming
a role that has it and then tightening nothing, because the second control below
governs which roles a re-plan may introduce.

**Second control: which roles a re-plan may use.** Organisation policy gains
`policy.replan.allowed_roles` (`docs/07` section 4). An agent-proposed step or
`replace_inputs` may use only a role on that list. Otherwise the patch becomes a
**pending human decision** (section 15.5), not a rejection.

Validation enforces both: a step whose `write_access` exceeds its role's is a
blocking error, and a patch introducing a role outside `allowed_roles` is a
pending decision when the proposer is an agent.

### 11.1 Runtime selection is role mapping in the organisation layer

A role does not name a model or a runtime product. Review 07 Q3 gives every
provider type several named instances with one default; the organisation layer
maps a role to an instance.

```yaml
# Organisation layer (docs/07 section 4)
roleMapping:
  implementer: { agentRuntime: claude-code, model: default }
  reviewer:    { agentRuntime: copilot-cli, model: default }
```

A role that named `claude-code` would break in an installation that ships only
`copilot-cli`. The first draft's role example hard-coded both a model and a
runtime; that is exactly the "templates hard-code installation-specific names"
failure from brief lesson 5.

### 11.2 What each layer may do to a library role

| Layer | May | May not |
|---|---|---|
| Library | Define the role | — |
| Organisation | Map the role to a runtime instance and a model; **tighten** `write_access`; **remove** tools (review 10 B11) | Relax `write_access`; **add** tools |
| Repository | Nothing | Anything (review 09 D9) |
| Run | Nothing | Anything |

Tightening is one-directional: `write_access` may only move towards `none`, and
`tools` may only shrink. Validation compares the effective role against the
library role and rejects any change in the other direction.

---

## 12. Tasks and skills (L5)

Review 09 D5.

### 12.1 References

One format, decided in `docs/11` section 3: `<package>:<type>/<name>`, with an
optional version constraint `<package>@<constraint>:<type>/<name>`.

```yaml
inputs:
  task_ref: feature-flow-default:tasks/implement-ticket
  skills: [feature-flow-default:skills/patch-review]
```

### 12.2 How parameters reach a task

A task is a Markdown document. It is rendered by the engine before the step
starts.

- The rendering language is **CEL**, the same language as everywhere else.
- `{{ <cel> }}` marks an expression inside task text. Everything else is literal.
- The binding context is the step's `inputs.bindings` map plus `params`, `run`
  and `steps`.

```markdown
# Implement ticket {{ params.ticket_id }}

Repository: {{ bindings.repository }}

The acceptance criteria are in the plan artifact at:
{{ bindings.plan_path }}

Work only on the branch you are given. Do not push to the default branch.
```

```yaml
inputs:
  task_ref: feature-flow-default:tasks/implement-ticket
  bindings:
    repository: { expr: "run.repositories[0]" }
    plan_path: { from_artifact: "steps.plan_spec.artifacts.plan" }
```

**A consumed artifact renders as a path, not a hash** (review 10 B6). The second
draft rendered `{{ bindings.plan_ref }}`, which is an artifact store reference the
agent cannot open. A binding of the form `from_artifact` is copied into the
sandbox by `CopyInAsync` (`docs/05` section 2) and renders as the **path** the
agent can read. A binding that is genuinely a reference — something the agent
should quote back but not open — uses `expr` and renders the reference.

### 12.3 Where the rendered prompt is stored

The rendered task is written to the artifact store and is content-addressed. The
step's `task_ref` at execution time points at the **rendered** artifact.

The run records both:

| Record | Value |
|---|---|
| Source | Package, version, content path, content hash |
| Rendered | Artifact hash |

That is what lets an audit answer "what exactly did the agent read" after the
package has moved on, and it is what makes a mid-run override auditable
(`docs/11` section 6).

---

## 13. Child plans (L9)

Review 09 D2. The first draft hard-coded three ticket ids in a plan whose whole
purpose was to generate the tickets.

### 13.1 The mechanism

1. A planning step (`agent.run` with a planner role) produces a **ticket-list
   artifact** — a typed artifact (section 10) listing tickets with their
   repositories and parameters.
2. `plan.replan` consumes it and returns a **patch** that adds one `run.child`
   step per ticket to a `parallel` step, each with `plan_ref` and bound
   parameters taken from the artifact.
3. The interpreter applies the patch through `applyPatch`. The branches are now
   ordinary steps: validated, auditable, and visible in the readable form.

**The expansion is deterministic** (review 10 B1). Turning a typed `ticket-list`
artifact into one `run.child` per item is a mapping, not a judgement. The second
draft used an agent for it, which adds cost and a failure mode to a
transformation that cannot fail if the artifact validates.

`plan.replan` therefore takes a **template mapping** and runs no agent:

```yaml
- id: expand_tickets
  kind: plan.replan
  inputs:
    mode: expand
    from: { expr: "steps.plan_tickets.artifacts.tickets" }
    mapping:
      template: feature-flow-default:templates/ticket-delivery
      target: tickets                  # the parallel step to add branches to
      bind:
        ticket_id: "$.ticket_id"
        repositories: "$.repositories"
    resume_at: tickets
  routes:
    replan_requested: { resume: true }
    failed: gate_epic_plan
    attempts_exhausted: gate_epic_plan
```

`$.<field>` is a path into each item of the artifact, not CEL — the mapping is
data, and no expression is evaluated. An agent may still be used for a re-plan
that needs judgement (`mode: agent`); the deterministic form is the default for
expansion. L9's reasoning is unaffected: either way a **patch** is produced.

**An expansion of zero items is not an error** (review 12 B5b). A ticket list
with no tickets, or a repository list with one entry, are both legitimate
outcomes of planning. The rules:

| Case | Behaviour |
|---|---|
| Zero items, `shape: parallel` | The patch adds no branches. The `parallel` step has an empty branch list and **completes immediately** with `completed`. It does not wait, and it does not fail |
| Zero items, `shape: chain` | The patch adds no steps. `target` routes directly to `on_other` |
| Zero items and no `on_other` | A **blocking validation error at patch time**. With nothing to chain, the route out of `target` is undefined, so the plan would have a dangling route |

The third case is the only one that blocks. An empty expansion is a valid plan
that does less work, not a failure — and treating it as an error would make a
planning step that finds nothing to do indistinguishable from one that broke.

### 13.2 Why a patch and not dynamic binding

The alternative — a `parallel` step whose branches are bound directly from the
artifact at execution time — is rejected.

| | Patch adding `run.child` steps | Dynamic binding from an artifact |
|---|---|---|
| Validated before execution | Yes, by `applyPatch` | No; the branches do not exist at load |
| In the readable form | Yes | No |
| Auditable as a diff | Yes | No |
| Deterministic under replay | Yes | Depends on artifact content read in an activity |

Dynamic binding would also make the plan unvalidatable at load, which breaks F3
and the whole of section 16.

### 13.3 How a child re-plan reaches the parent

A child run returns `replan_parent` with a patch for the **parent** plan. The
parent applies it through the same `applyPatch` (`docs/01` 3.4). Already-completed
steps are untouched; protected steps are intact. This is the `docs/01` section 6.2
scenario, worked in `docs/13` section 3.

**The interpreter handles it; it is not routed** (review 11 A1). A branch must
not route `replan_parent` to an agent `replan` step, because that step would
re-plan from its own inputs — `steps.plan_tickets.outcome` in the epic example —
and **discard the child's patch**. The child's discovery would be lost, which is
precisely what `replan_parent` exists to prevent.

The patch goes through `applyPatch` with the same three results as any other
(section 15.5): applied, pending human decision, or rejected. **Sibling branches
keep running**; a `replan_parent` from one branch does not suspend the others.

---

## 14. Policy references (L12)

Review 09 D3 and C7. A template names a **decision type**. It does not contain
policy.

```yaml
- id: merge_gate
  kind: human.gate
  gate:
    decision_type: ticket.merge_approval
  inputs:
    prompt_ref: feature-flow-default:prompts/merge-approval
    context_refs: { expr: "[steps.review.artifacts.findings]" }
  routes:
    approved: merge_pr
    rejected: { end: rejected }
```

| Question | Answered by | Where it lives |
|---|---|---|
| What is being decided | The plan: `gate.decision_type` | Template |
| Who may approve, minimum count, whether an agent counts | `policy.approval` | Organisation layer (`docs/07` 4) |
| Whether it may be decided from chat | `policy.chat.allowedDecisions` | Organisation layer |
| Whether it may be auto-approved | `policy.auto_approve`, and floor F1 for default-branch merges | Organisation layer, bounded by the floor |

The first draft put `required_role` and `min_approvers` in the gate's inputs.
That is policy in a template, which is what L12 exists to prevent, and it would
let a template relax an organisation's approval rule.

### 14.1 A gate may record a choice, not only approve or reject

Review 10 B12. `docs/01` 7.3 gives a partial delivery three human choices:
retry, fix forward, revert. Modelling those as three outcomes would add outcomes
to the vocabulary for one step.

> **A gate keeps `approved` and `rejected` as its outcomes. It records a
> `choice`, and guarded routes select on it.**

```yaml
- id: delivery_gate
  kind: delivery.gate
  gate: { decision_type: delivery.partial_approval }
  inputs:
    status: partial
    options: [retry, fix_forward, revert]
  routes:
    approved:
      - when: "steps.delivery_gate.choice == 'revert'"
        to: revert_prs
      - when: "steps.delivery_gate.choice == 'fix_forward'"
        to: replan
      - to: deliver
    rejected: { end: partial }
```

No new outcomes, and the choice is visible in the audit record and in the
readable form.

`decision_type` values are namespaced strings (`ticket.merge_approval`,
`epic.delivery_approval`, `delivery.partial_approval`). The organisation's
`policy.approval` is keyed by them. An unknown decision type is a blocking
validation error at run start.

---

## 15. Patches

### 15.1 The patch document

Review 09 E3 and E4. A patch is its own document, not part of the plan.

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-1234
  proposed_by: { type: agent, step_id: replan }
  reason: "implementation discovered a second repository"
base_revision: 7
changes:
  - op: add_branch
    target: tickets
    step: { id: ticket_d, kind: run.child, inputs: { plan_ref: "..." } }
```

`base_revision` is **optimistic concurrency**. A patch whose `base_revision` is
not the current revision is rejected as stale and re-proposed against the current
one. It is not silently rebased.

### 15.1.1 The patch names its resume point

Review 10 A6. The second draft's `plan.replan` routed
`replan_requested: implement` regardless of what the patch did, so a patch that
inserted a step before `implement`, or fixed the plan somewhere else entirely,
was skipped or ignored.

```yaml
base_revision: 7
resume_at: implement
changes: [ ... ]
```

| Rule | Detail |
|---|---|
| `resume_at` | A step id, validated exactly like a route target: it must exist after the patch is applied |
| Meaning | `plan.replan`'s `replan_requested` route means "continue at the patch's resume point", not "go to this fixed step" |
| Default if omitted | The step that routed to `plan.replan` — that is, the step whose outcome sent the run here. This is the common case: a step fails, asks for a re-plan, and wants to retry itself |
| Validation | A `resume_at` that does not exist after the patch is a blocking error |

So the route becomes a constant: every `plan.replan` step routes
`replan_requested: { resume: true }`, and the interpreter resolves the target
from the patch. A plan author no longer has to guess where a re-plan will land.

`{ resume: true }` is the **only** form (review 11 A3). The third draft also used
`{ end: resume }`, which wrongly made resuming a run status.

### 15.2 "Weaken" is defined

Review 09 E1. Floor F3 says a protected step cannot be removed or weakened.
"Weakened" means any of:

| Operation on a protected step | Blocked |
|---|---|
| Removing it | Yes |
| **Bypassing it** — any change that makes it unreachable from the plan's entry point | Yes |
| Changing its `inputs` | Yes |
| Changing any route **out** of it | Yes |
| Changing its `limits` | Yes |
| Changing its role | Yes |

Bypassing is the case the first draft missed: `change_route` could make a
protected step unreachable without removing it. Reachability is therefore a
validation check (section 16.3).

### 15.3 Operations

Ten operations. `set_parameter`, `override_library_reference` and
`insert_policy_guard` from the first draft are **removed** (review 09 E5): the
first two duplicate parameter binding and mid-run override respectively, and the
third was never defined.

| Op | Effect |
|---|---|
| `insert_before` | Insert a step before `target`. Section 15.3.1 |
| `insert_after` | Insert a step after `target`. Section 15.3.1 |
| `replace_step` | Replace the step at `target` |
| `remove_step` | Remove the step at `target` |
| `replace_inputs` | Replace the `inputs` of `target` |
| `replace_limits` | Replace the `limits` of `target`, within ceilings (F4) |
| `replace_role` | Replace the role of `target` |
| `change_route` | Change one route of `target` |
| `add_branch` | Add a branch to a `parallel` at `target` |
| `remove_branch` | Remove a branch from a `parallel` at `target` |
| `add_chain` | Add a step chained after `target`: `target`'s `completed` route goes to the new step, every other outcome goes to `on_other`. Section 15.3.2 |

### 15.3.1 `insert_before` and `insert_after` are defined

Review 10 A7. On a graph, "insert after X" is ambiguous: which of X's routes get
rewired? The second draft used `insert_after epic_review` **and** a
`change_route` on `epic_review` in the same patch, which is either redundant or
required, and the document did not say which.

**Decision: keep the two inserts, with rewiring defined.**

> `insert_after X` inserts `S` immediately after `X`. Every route of `X` that
> pointed at some step `Y` now points at `S`. `S`'s own routes are given
> explicitly in the patch. `insert_before X` is the mirror: every route from any
> step that pointed at `X` now points at `S`, and `S` routes to `X`.

Why keep them rather than drop them for `add_step` plus `change_route`:

| | `insert_after` with rewiring | `add_step` plus `change_route` |
|---|---|---|
| Operations to insert into a chain | 1 | 2, and the author must know every inbound route |
| Failure mode if the author forgets | None; rewiring is automatic | A dangling route, or a step that is added but unreachable |
| What an LLM must get right | The step and its own routes | The step, its routes, **and** every route that must be repointed |

An LLM writing a patch knows where it wants the new step to sit. It does not
reliably know every inbound route to `X`, which is exactly the information
`add_step` would force it to supply. Rewiring is the version with fewer things to
get right, so it is the one kept.

The consequence: `insert_after` followed by `change_route` on the same target is
**redundant**, and the second draft's example is fixed in `docs/13` section 3 to
use `insert_after` alone.

### 15.3.2 Ordered work is a chain, not a concurrency limit

Review 11 A2. The third draft expressed ordered two-repository delivery as a
`parallel` with `max_concurrency: 1`. That is wrong twice over:

- With `join: all`, if the first repository fails, the second **still runs**,
  because the join waits for every branch. A failed delivery does not stop the
  next one.
- "In the order added" is an implicit property of a concurrency limit, not a
  declared dependency. Nothing in the plan says the second depends on the first.

**Decision: `mode: expand` gains a `chain` shape.** Order is a declared
dependency, expressed as routes.

```yaml
- id: expand_delivery
  kind: plan.replan
  inputs:
    mode: expand
    shape: chain
    from: { expr: "params.repositories" }
    mapping:
      template: feature-flow-default:templates/repository-delivery
      target: delivery_start
      on_other: delivery_gate
      bind:
        repository: "$"
    resume_at: delivery_start
```

which produces one `add_chain` per item:

```yaml
changes:
  - op: add_chain
    target: delivery_start
    on_other: delivery_gate
    step:
      id: deliver_api
      kind: delivery.deploy
      inputs: { repository: acme/api }
  - op: add_chain
    target: deliver_api
    on_other: delivery_gate
    step:
      id: deliver_web
      kind: delivery.deploy
      inputs: { repository: acme/web }
```

`add_chain` rewires `target`'s `completed` route to the new step and points every
**other** outcome of `target` at `on_other`. So `deliver_api` completing goes to
`deliver_web`; `deliver_api` failing goes to `delivery_gate` and `deliver_web`
never runs. That is the ordering the brief asks for, and it is visible in the
plan rather than implied by a number.

**Rejected: `order: sequential` with `stop_on: any_failed` on `parallel`.** It
would keep the fan-out shape for work that is not fan-out, and it would need a
second mechanism (`stop_on`) to express what a route already says. The chain
reuses routing, which is the mechanism the language already has.

### 15.4 Who may use which operation

Review 09 E6.

| Op | An agent may propose | A human may apply |
|---|---|---|
| `insert_before`, `insert_after` | Yes | Yes |
| `replace_step` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `remove_step` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `replace_inputs` | Yes; if `target` is protected, or the role is outside `policy.replan.allowed_roles`, a **pending human decision** | Yes |
| `replace_limits` | Yes, within `policy.attempts.ceiling` | Yes, up to `hardCeiling`; beyond that needs admin (F4) |
| `replace_role` | **No** | Yes, `steerer` or `admin` |
| `change_route` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `add_branch` | Yes, if the `parallel` has not started | Yes |
| `remove_branch` | Yes, if the branch is not protected and has not started | Yes |
| `add_chain` | Yes, if `target` has not started | Yes |

Review 10 B3: the second draft's "Yes, if `target` is not protected" read as
*rejection*. It is not. An agent patch touching protected content becomes a
**pending human decision** (section 15.5), which a human may approve with a
reason. Only a patch that would relax a safety floor is rejected outright.

### 15.5 A patch touching protected content becomes a human decision

Review 09 E2, decided by the product owner 2026-10-27. Brief lesson 3: a re-plan
silently changed a ratified detail.

- An **agent** patch that touches protected content is **not rejected**. It
  becomes a **pending human decision**, recorded with the diff and the reason.
- A **human** may approve it with a reason. The approval is audited like any
  steering action.
- **Safety floors stay absolute.** No patch, human or agent, relaxes F1-F4. A
  patch that would relax a floor is rejected outright, with no human override
  path.

So there are three outcomes for a patch, not two: applied, pending human
decision, rejected.

### 15.6 Mid-run library override is not a plan patch

Review 09 E5. Overriding library content for a running run is a **separate
audited operation** (`docs/11` section 6), not a patch operation. It changes what
content future steps resolve; it does not change the plan. Keeping them separate
means the plan revision history stays a record of *structure*, and the override
log stays a record of *content*.

---

## 16. Validation

Full table: `docs/12-validation-and-testing.md`. This section states the rule and
the checks review 09 F2 and F3 asked for.

### 16.1 The blocking rule

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

Warnings are listed in the run start record. They never change behaviour, and a
template cannot register a runtime warning (review 09, answer 12: static only).

### 16.2 Phases

| Phase | Blocks on |
|---|---|
| Template load | Schema, undeclared references, unknown kinds, duplicate ids, expression syntax and types, contract compatibility, unrouted outcomes, cycles without an exit, unreachable steps |
| Binding (run start) | Unbound required parameters, library resolution, lock creation, capability negotiation, policy eligibility, unknown decision types |
| Patch | Everything in load, plus protection invariants, `base_revision` staleness, and the operation's authorisation |
| Step start | Inputs satisfy the kind's contract; consumed artifacts exist and validate |

### 16.3 The checks that were missing

| Check | Blocks |
|---|---|
| Unreachable steps | Yes |
| Every outcome the kind can return is routed | Yes |
| Every cycle has a route out | Yes |
| Producer and consumer contract compatibility | Yes |
| Expression type check | Yes |
| **Protected-step reachability** — no patch may make a protected step unreachable | Yes, on patch |

---

## 17. Changes this document makes to engine documents

Review 09 A4: "If you change the engine docs, say so and update them."

| Document | Change | Why |
|---|---|---|
| `docs/06` 3.1 `human.gate` | Inputs `required_role` and `min_approvers` removed; `gate.decision_type` added | C7, L12: policy is not template content |
| `docs/06` 3.2 `human.session` | `session_id` removed (engine-generated); `checks` replaced by `checks_from` (step ids whose validators to offer) | C9, D23 |
| `docs/06` 6.5 `scm.merge` | `method` removed; `require_human` removed | C2: the organisation allows methods, the repository chooses; F1 is a floor |
| `docs/06` 7.4 `test.run_tiers` | `tiers` replaced by `tier_tags`; `execution_target` removed | C1: tiers come from `feature-flow.yaml`, read per step |
| `docs/06` 10.1 `fix.verify` | `regression_test_required` removed | C4: the regression test requirement is a step invariant, not a template switch |
| `docs/08` | Spike 1 measurements gain worker slot capacity; the Temporal-unavailable injection states re-attach by handle; `wait.for_state` poll form stated | H1, H2, H3 |

Each is applied in the named document.

---

## 18. Open questions

None open. Every question from the first and second drafts was answered in review
10 or 11:

| Question | Answer |
|---|---|
| CEL fallback: a hand-written subset with a published grammar | **Acceptable.** Decided by the product owner, 2026-10-27. Spike 1 runs the checks (section 8.1) |
| `end: partial` and the three delivery choices | A gate keeps `approved` / `rejected` and records a **choice**; guarded routes select on it (section 14.1) |
| `compatible_with` or structural compatibility | Declared `compatible_with`. Explicit, checkable, and an author decision rather than an accident of shape |
| `run.wait` versus `parallel` | **`run.wait` is out of the version 1 language** unless a worked example needs it. Removed from the schema enum, reserved in `docs/06` |
| Per-kind input schemas: generated or hand-written | Generated, in implementation. Recorded as an implementation task |
| Artifact type compatibility | Declared `compatible_with` (section 10.3) |

**Two measurements remain**, both assigned to spikes rather than open as design
questions:

| Item | Where |
|---|---|
| The `outputs` cap of 16 entries and 256 bytes is a starting value | Spike 2 records actual output sizes per kind (review 11 C1) |
| `insert_after` with rewiring is argued, not measured | Spike 1 or 2: have the planner agent author patches with both forms on the fixture plans and record validation failure rates (review 11 C2) |
