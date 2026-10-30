# Workflow definition language

Status: design. Updated 2026-10-31 with the spike results (`docs/15` Spike 1,
`docs/16` Spike 2, `docs/18` Spike 3), the merge design (`docs/20`) and the code
review design (`docs/19`).

This document defines the language workflows are written in: plans, templates,
routing, expressions, artifact contracts, roles, tasks and patches. Every step
kind, outcome and input name comes from `docs/01` (architecture), `docs/06`
(step catalogue) and `docs/07` (configuration). Where this document changed one
of those, section 17 lists the change.

| Companion | What is there |
|---|---|
| `docs/10-workflow-schema.md` | The JSON Schema for plans and patches |
| `docs/11-library-and-versioning.md` | Library packaging, the version lock and mid-run override |
| `docs/12-validation-and-testing.md` | The full validation table and how workflows are tested |
| `docs/13-workflow-examples.md` | Worked examples |
| `docs/14-workflow-decision-records.md` | Decision records L1 to L12, the language decisions cited in headings below |

Design requirements DR1 to DR13 are listed in `docs/08` section 1.3. Each is
explained where this document uses it.

---

## 1. Sources of truth

One fact lives in one place.

| Fact | Owner | This document |
|---|---|---|
| The set of step kinds, and each kind's inputs, outcomes, form, scope, idempotency, limits and audit | `docs/06-step-catalogue.md` | Section 3 restates the kind list and the outcomes each kind may return. It does not redefine contracts |
| The outcome vocabulary | `docs/06` section 1.1, plus `any_failed` (`docs/01` section 3.3.1, parallel joins) and `not_approved` (`docs/06` section 3.3, `policy.auto_approve`) | Section 4 |
| Step field names and naming convention | `docs/01` section 3.3 (plan as data) | Section 5 |
| Join semantics | `docs/01` section 3.3.1 (parallel branches and joins) | Section 7 |
| Policy objects and who may approve | `docs/07` sections 4 (organisation schema) and 7 (policy model) | Section 14 |
| Plan schema, patch schema, expression language, artifact contracts, library packaging | **This document and `docs/10` to `docs/13`** | — |

**Naming convention, by document family:**

| Family | Convention | Examples |
|---|---|---|
| Plans, patches, library content, artifact schemas | **snake_case** | `task_ref`, `write_access`, `max_concurrency`, `tier_tags`, `base_revision`, `resume_at` |
| Configuration (`docs/07`) | camelCase | `roleMapping`, `agentRuntime`, `hardCeiling`, `minCount` |

Configuration stays camelCase because it is YAML read by .NET binding, it is
already written for three profiles, and renaming it would gain nothing. The two
families meet at `policy.replan.allowed_roles` (section 11.0), which is
configuration, and at `roleMapping`, which is configuration that names library
roles.

In the plan and library family the key is `artifact_types` (not
`artifactTypes`), and there is no `schemaVersion`: `apiVersion` carries the plan
schema version.

---

## 2. Concepts

| Concept | Meaning |
|---|---|
| Interpreter | The engine component that executes a plan, one step at a time or several in parallel (`docs/01` section 3) |
| Plan | The data the interpreter executes. The current revision lives in workflow state; every revision is in the artifact store |
| Template | A plan with unbound parameters. Same schema as a plan (L1) |
| Plan revision | An immutable, numbered version of a plan. A re-plan produces a new revision |
| Step | A node in the plan: `id`, `kind`, `scope`, `inputs`, `requires`, `limits`, `routes`, `protected`, optional `gate`. Section 5 |
| Outcome | Exactly one result per execution of a step. The routing vocabulary. Section 4 |
| Route | Maps an outcome to the next step id, or to a terminal directive. Section 6 |
| Terminal directive | `{ end: <status> }`: ends the run with that status. Not a step id |
| Patch | An ordered list of domain operations against a named `base_revision`. Section 15 |
| Library | Versioned workflow content: templates, roles, tasks, skills, checks, artifact types (`docs/11`) |

### 2.1 One schema (L1)

A template and a plan are the same document. The difference is whether
`parameters` are bound.

- A template declares `parameters` with types, defaults and constraints.
- A run binds them. Binding produces plan revision 1.
- A re-plan produces revision 2, 3, and so on, in the same schema.

There is no second syntactic form and no template-to-plan compilation step.

---

## 3. Step kinds

Forty kinds, taken from `docs/06`, which owns their contracts.

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

- **Steering is not a step.** It is a control-plane capability that produces a
  patch or an attempt grant (`docs/01` section 7).
- **`run.wait` is not in the version 1 language.** `parallel` with `run.child`
  branches covers every case in the worked examples. It is reserved in `docs/06`
  section 4.2 and comes back only with an example that needs it.
- **How branches merge** is the merge design in `docs/20`: catch up with the
  target branch, re-test if that changed anything, then merge; no merge lock.
  `scm.merge` takes `engine_can_merge` (true: the engine builds the merge or
  squash commit from exactly what was tested and pushes it; false: the engine
  raises a human decision request, waits for a human to merge on the code host,
  detects the merge and continues) and `squash` (true for a squash merge). Usual
  settings: ticket into epic branch, squash, engine merges; epic into project
  branch, no squash, engine merges; project branch into `main`, no squash,
  engine does not merge by default (floor F1, section 14). The source control
  steps are in `docs/06` section 6.

---

## 4. Outcomes

### 4.1 Vocabulary

From `docs/06` section 1.1, plus `any_failed` and `not_approved`.

| Outcome | Meaning |
|---|---|
| `completed` | The step did its work |
| `blocked` | The step cannot proceed; something external must change |
| `failed` | The step did its work and the work was wrong or rejected |
| `attempts_exhausted` | The step's attempt allowance is spent. Never a silent failure (`docs/01` section 3.7, attempt budgets) |
| `replan_requested` | The step produced a plan patch |
| `replan_parent` | A child run asks its parent to re-plan |
| `partial` | Some of the work happened (delivery across several repositories) |
| `approved` | A human or policy approved |
| `rejected` | A human rejected |
| `not_approved` | `policy.auto_approve` did not approve; a human gate follows |
| `findings_open` | Blocking review findings remain |
| `base_moved` | `scm.merge` was refused because the target branch moved since the ticket caught up. Routed back to the catch-up step |
| `ready` | The merge bar is met (decision D8: no unresolved blocking finding, and every finding fixed or declined with a recorded reason) |
| `any_failed` | Aggregate outcome of `parallel` (and the reserved `run.wait`) when a branch fails and the join is not satisfied |

### 4.2 Which outcomes each kind may return

Every step must route every outcome its kind can return, or the template fails
validation (section 6.1). The interpreter produces `attempts_exhausted` for any
step whose allowance is spent, so every step that consumes attempts must route
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
| `scm.merge` | `completed`, `blocked` (checks or protection), `base_moved`, `rejected`, `attempts_exhausted` |
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

- Local, pure steps (`plan.validate`, `policy.auto_approve`,
  `findings.evaluate_readiness`, `delivery.deploy_order`, `artifact.validate`)
  do not consume attempts, so they do not return `attempts_exhausted`.
- A child run that ended without reaching an `end:` route has run status
  `incomplete` (DR6: such a run never reports `completed`). Its parent's
  `run.child` step returns `failed` for it.

---

## 5. Step fields

From `docs/01` section 3.3 (plan as data). One field set, snake_case.

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
| `id` | Yes | Unique in the plan. **Must not look like a step kind**: `run_tests`, not `test.run_tiers`, so a step id is never confused with a kind or a directive |
| `kind` | Yes | From section 3 |
| `scope` | No | `project`, `epic`, `ticket`, `delivery`, `any`. Defaults to the plan's scope |
| `inputs` | No | Per kind, from `docs/06`. Artifact references, never inline content |
| `requires` | No | Provider capabilities. Checked at run start (capability negotiation, decision S3) and again if `feature-flow.yaml` changes |
| `limits` | No | `attempts`, `start_to_close`, `heartbeat`, `correction_turns` (section 10.3). Within policy ceilings (floor F4: attempt budgets stay within the ceiling; beyond the hard ceiling needs the admin role). Every limit has an engine default, overridable by the workflow, a child workflow and the step |
| `routes` | Yes | Outcome to step id or terminal directive. Section 6 |
| `protected` | No | Cannot be removed or weakened by a re-plan or by steering (floor F3; section 15.2) |
| `gate` | No | A decision type, not policy content. Section 14 |
| `produces` / `consumes` | No | Artifact contracts. Section 10 |
| `outputs` | No | Small typed values the step publishes to workflow state. Section 8.3.2 |

---

## 6. Routing

### 6.1 One form: per-step `routes`

Each step owns its routes. There is no top-level `routes` list.

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

- A route value is a **step id**, a **terminal directive** (section 6.2), or an
  **ordered list of guarded routes**.
- Guards are CEL expressions (section 8), evaluated in order; the first true
  guard wins.
- A guarded list **must** end with an unguarded fallback. Validation enforces it.
- An unrouted outcome that the kind can return is a **blocking** validation
  error.
- A route may point at a step that has already run. That step runs again
  (section 6.3).

### 6.2 Terminal directives

A route target is either a step id or a directive, never both. A directive is a
YAML mapping, not a string, so it needs no quoting and the schema checks it
without a regex.

```yaml
routes:
  completed: { end: completed }
  rejected: { end: rejected }
```

| Directive | Run status |
|---|---|
| `{ end: completed }` | The run succeeded |
| `{ end: failed }` | The run failed |
| `{ end: partial }` | Delivery: some repositories delivered (`docs/01` section 7.3, partial delivery) |
| `{ end: rejected }` | A human rejected and the plan has nowhere else to go |

**A run ends only by reaching a directive.** If the interpreter has nothing
ready to run and no step has routed to `{ end: ... }`, the run ends with status
**`incomplete`**, never `completed` (DR6: a run that ends without reaching an
`end:` route never reports `completed`). `incomplete` is a run status, not a
directive: no route can target it. A parent's `run.child` step sees an
`incomplete` child as `failed`. Spike 2 showed why this matters: an interpreter
that could not loop stopped with nothing ready and reported `completed` without
a pull request (`docs/16` section 12).

**Resuming after a re-plan is not a directive.** `end` values are run statuses,
and resuming is not one: the run has not ended. Resuming has its own form:

```yaml
routes:
  replan_requested: { resume: true }
```

`{ resume: true }` is valid only on a `plan.replan` step. It sends the run to
the patch's `resume_at` step (section 15.1.1). The `end` enum in `docs/10` does
not include `resume`.

### 6.3 Loops are bounded by the engine

A plan may contain a cycle. Test, fix, re-test is the normal case. The plan does
not count iterations; the engine bounds them.

| Rule | Mechanism |
|---|---|
| Re-entry runs the step again | Routing to a step that already has an outcome runs it again (DR12). Its current outcome, outputs and artifacts are cleared, so a route condition never mistakes a stale value for the latest. The previous execution's results stay readable as `steps.<id>.previous`, and `steps.<id>.executions` counts executions (section 8.3). This is what makes a loop work |
| Per-step allowance | `limits.attempts`. Every entry into the step counts, whatever routed there (`docs/01` section 3.7). The allowance is checked on every execution, including when a loop re-enters the step (DR10). Clearing the outcome does not reset the count. Like every limit, it has an engine default, overridable by the workflow, a child workflow and the step |
| Merge refusals | `scm.merge` returning `base_moved` routes back to catch-up and re-enters the merge step, so its attempt allowance (default 3, counting every attempt including re-entry after conflict resolution) caps the refusals; then `attempts_exhausted` goes to a human |
| Per-run ceiling | `policy.attempts.ceiling` (`docs/07` section 4) |
| Findings loop | Governed by the findings ledger, not a count (`docs/17` section 6.5). The ledger is the source of truth and each review pass is evidence (position L1). A finding verified fixed stays fixed while its code is unchanged; a re-raise there is recorded, not acted on (L2). A finding that reappears after being fixed twice **escalates** instead of looping (`docs/06` section 9.1). A declined finding never restarts the loop |
| Exhaustion | Surfaces as `attempts_exhausted`, which the plan routes to a re-plan or a human gate. Never a silent failure |

Why the check on every execution matters: in Spike 3 a loop whose re-entries
were not checked ran its fix step 213 times in 15 seconds (`docs/18` section 4).

Validation requires **every cycle to have a route out**: at least one step in
the cycle must route an outcome to a step outside the cycle or to a terminal
directive. A cycle with no exit is a blocking error (section 16.3).

---

## 7. Parallel and joins

A `parallel` step fans out to branches and joins them. From `docs/01` section
3.3.1.

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

- `any_failed` is the aggregate route when a branch fails and the join is not
  satisfied.
- `max_concurrency` bounds how many branches are in flight. A waiting branch
  starts **as soon as a running branch finishes and frees a slot**, not when a
  whole batch finishes (DR7). Spike 3 found the batch form made a sibling wait
  on the slowest branch (`docs/18` section 4).

### 7.0 What a branch reports, and what it may route to

Terminal directives are **run** statuses (section 6.2). A branch that routed
`completed: { end: completed }` would end the whole run when one ticket
finished. So a branch does not route; it reports.

> **A `run.child` branch reports only its terminal outcome and `replan_parent`
> to the parent. It routes nothing else. `blocked` is handled inside the child's
> own plan.**

| Branch outcome | Who handles it | Effect on the join |
|---|---|---|
| `completed` | The join | Counts towards `all`, `any` or `n`. Only reported if the child reached `{ end: completed }` |
| `failed` | The join | Counts towards `any_failed`. Includes a child that ended `incomplete`, without reaching an `end:` route (DR6, section 6.2) |
| `attempts_exhausted` | The join | Counts towards `any_failed` |
| `replan_parent` | **The interpreter** | The child's patch goes through `applyPatch` (the single path every patch takes, `docs/01` section 3.4). **Siblings keep running.** The branch **counts as completed for the join** |
| `blocked` | The child's own plan | Never reaches the parent |

Rules for `replan_parent`:

- **It counts as completed for the join.** The child did its work and reported a
  discovery; that is not a failure, and leaving the branch unresolved would
  deadlock a `join: all`.
- **The interpreter handles it; it is not routed.** Routing it to an agent
  `replan` step would re-plan from that step's own inputs and discard the
  child's patch. The interpreter applies the child's patch with the three
  possible results of any patch: applied, pending human decision, rejected
  (section 15.5).
- **Siblings keep running.** The patch applies to future steps; in-flight
  branches are untouched, as for any re-plan that arrives while a branch is in
  flight (`docs/08` section 2.1; confirmed by Spike 1, `docs/15` section 3.7).
- **The ticket is not automatically re-run.** If the patch inserts a step or
  re-points a route so the child's work is revisited, the child runs again; if
  it only adds future work, it does not. The interpreter does not infer a
  re-run, because that would silently spend the child's attempt budget.

A branch therefore has **no `routes`** in the common case. It may not use a
directive and may not route a terminal outcome; validation enforces both. The
`parallel` step's own `routes` (`completed`, `any_failed`) are where the
run-level decision is made.

### 7.1 A ticket branch is a `run.child`

A branch is a step or a sub-plan. A ticket branch is a `run.child` step carrying
`plan_ref`.

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

A re-plan may add branches to a `parallel` step that has not started. It may not
remove a protected branch (`docs/01` section 3.3.1).

The same shape works one level up: a **project** run (scope `project`) starts
one `run.child` per epic, with `child_scope: epic`, the way an epic run starts
its tickets.

---

## 8. Expressions

### 8.1 The language is CEL

Expressions are written in the **Common Expression Language** (CEL), not a
look-alike.

Why CEL rather than a bespoke subset: it is a specification with conformance
tests; it is side-effect free and total by construction (no loops, no I/O, no
recursion); and it has a static type checker, which is what makes the
expression type check at template load possible (section 16.3).

**Implementation: Cel.NET** (NuGet `Cel.NET` 2.3.1,
<https://github.com/rayokota/cel.net>), a C# port of CEL-Java that tracks
`google/cel-spec`. Spike 1 checked it (`docs/15` section 9.2):

| Check | Result |
|---|---|
| A restricted checker rejects unregistered functions and unknown variables | Pass |
| Evaluation is deterministic under replay | Pass: 120,000 evaluations, one distinct result each, order-independent |
| Static cost estimation exists | Pass: `Cel.Cel.EstimateCost(object)` / `Cel.Prog.Cost()` |
| The cost limit is enforceable | Pass: no built-in setting, but `Cost.Max` plus the caps in section 8.5 work |
| Speed | Cold start to first evaluation 136 to 155 ms; about 2 ms warm load; 0.67 to 1.0 µs per evaluation |

Package facts: four target frameworks (`net10.0`, `net8.0`, `netstandard2.1`,
`net462`) and seven transitive dependencies, including
Antlr4.Runtime.Standard, Apache.Avro and Google.Protobuf.

Rules the engine must follow:

- **Cost estimation is the only stop.** Cel.NET has no runtime interrupt
  (`DecObserveEval` fires but the exception is swallowed), so a running
  expression cannot be stopped. The static cost estimate is what rejects an
  expensive expression before it runs. The length and AST-node caps in section
  8.5 are a second control.
- **Build the environment with `Env.NewCustomEnv` and explicit opt-in.**
  `Env.NewEnv()` and the default `ScriptHost` register the whole standard
  library and comprehensions before the engine declares anything, so a checker
  built on them is not restricted.
- **The engine registers `lower` and `upper`.** Cel.NET does not provide them,
  but section 8.4 allows them. They use culture-invariant conversion
  (`ToLowerInvariant`); the culture-sensitive form gives different results on
  different machines and so breaks replay.

**Maintainer risk.** Cel.NET has a small maintainer base. Exposure is limited:
CEL is used only for route guards, input binding and task rendering, so a forced
migration touches the expression evaluator, not the interpreter. The fallback,
accepted by the product owner on 2026-10-27, is a hand-written subset with a
published grammar.

### 8.2 Where expressions appear

| Place | Form |
|---|---|
| A `when` guard in a route | Bare string: `when: "findings.blocking_count > 0"` |
| An input value | Mapping: `repositories: { expr: "params.ticket_a_repos" }` |
| A parameter default or constraint | Bare string in the parameter declaration |
| A task body | `{{ <cel> }}` delimiters. Section 12.2 |

Anywhere else, a bare YAML string is a **literal**. In plan YAML an expression
is always the value of a `when` key or of an `expr` key; there is no third form.
`${...}` is not part of the language.

### 8.3 Variable namespaces

Four namespaces: `params`, `steps`, `run` and `findings`. Nothing else is in
scope.

| Namespace | Contents | Example |
|---|---|---|
| `params` | Bound run parameters | `params.ticket_id` |
| `steps.<id>.outcome` | The outcome of the step's current execution. Cleared, with its outputs and artifacts, when a route re-enters the step (section 6.3) | `steps.review.outcome == 'findings_open'` |
| `steps.<id>.artifacts.<name>` | An artifact **reference** produced by the step. Never content | `steps.plan_spec.artifacts.plan` |
| `steps.<id>.outputs.<name>` | A small typed value the step published. Section 8.3.2 | `steps.plan_spec.outputs.tier_tags` |
| `steps.<id>.previous` | The previous execution's results (its outcome, outputs and artifacts), kept when a loop re-runs the step | `steps.review.previous.outcome == 'findings_open'` |
| `steps.<id>.executions` | How many times the step has executed in this run | `steps.fix.executions < 3` |
| `steps.<id>.choice` | A choice recorded by a gate. Section 14.1 | `steps.delivery_gate.choice == 'revert'` |
| `run` | Run facts. Section 8.3.1 | `size(run.repositories) > 1` |
| `findings` | `findings.blocking_count`, `findings.open_count` | `findings.blocking_count == 0` |

**There is no `manifest` namespace.** Repository facts are read per step from
`feature-flow.yaml` and never copied into the plan (`docs/01` section 5.1.2), so
they cannot go stale. An expression that read manifest data would bring that
staleness back. There are no `facts` or `repo` namespaces either.

#### 8.3.1 The `run` namespace

| Field | Type | Source |
|---|---|---|
| `run.id` | string | The run id, assigned at start |
| `run.scope` | string | `project`, `epic`, `ticket`, `delivery`. From the run request |
| `run.repositories` | list of string | The run request. Not the manifest |
| `run.branch` | string | Resolved by the step that needs it, from the branch convention |
| `run.integration_branch` | string | Same |
| `run.head_sha` | string | **A reference resolved by the consuming step**, not a value in workflow state |
| `run.aggregate_diff` | string | Same. An artifact reference |
| `run.tracker_issue` | string | The run request |
| `run.delivery_ledger` | string | An artifact reference, read through an activity (`docs/01` section 7.3) |
| `run.delivery_prs` | list of string | Pull request references for the delivery, from the delivery ledger |

Anything that **changes during the run**, such as `head_sha` or
`aggregate_diff`, is a reference the consuming step resolves in its activity
(the part of a step that runs outside workflow code and may do I/O). It is not a
value in workflow state, because reading it there would be I/O in workflow code.

`findings` is a namespace, not a field of `run`: `run.findings` does not exist.

#### 8.3.2 Expressions never read artifact content

> **Expressions see references, outcomes and published outputs. Never content.**

Reading artifact content inside an expression (for example
`steps.plan_spec.artifacts.plan.verification.tier_tags`) would be I/O in
workflow code, which breaks deterministic replay (risks R2 and R12 in `docs/08`:
non-determinism leaking into workflow code). Where a step needs a value from an
artifact, the **consuming step** reads it in its own activity: `run_tests`
consumes the plan artifact and reads `tier_tags` itself, and the expression only
passes the reference.

Where a value genuinely must reach workflow state, a step may publish a small,
typed, size-capped `outputs` map:

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

The cap stops `outputs` becoming a second, looser artifact store inside workflow
state. A step that needs more should pass an artifact reference and read it in
an activity.

**Measured in Spike 2** (`docs/16` section 2): across eleven step kinds in a real
run, the widest value was 71 bytes and the most entries 5. The cap never bound,
so it is kept as it is.

### 8.4 Allowed functions

Predicates and selection only. No comprehensions.

`size`, `contains`, `all`, `any`, `exists`, `startsWith`, `endsWith`, `lower`,
`upper`, plus arithmetic, comparison and boolean operators. `lower` and `upper`
are registered by the engine (section 8.1).

**`matches` is not in version 1.** It brings a regex engine into workflow code,
and a backtracking regex can take exponential time on a hostile input; workflow
code must not delay a heartbeat (section 8.5). No worked example uses it. If it
is needed later, reinstate it with .NET's non-backtracking regex and the length
caps, and re-check that the cost estimate covers it.

### 8.5 Bounds

| Bound | Value |
|---|---|
| Loops, recursion, I/O, clock, randomness | Impossible: not in CEL, and the engine registers no such function |
| Expression length | 1000 characters, checked at load |
| AST nodes | 200, checked at load |
| Evaluation cost | CEL's static cost estimate, capped; the checker rejects an expression over budget |
| Evaluation time | Bounded by the cost cap. Evaluation is pure workflow code, so it must not delay a heartbeat |

---

## 9. Parameters and binding

A template declares parameters; a run binds them at start.

```yaml
parameters:
  ticket_id:
    type: string
    required: true
  ticket_a_repos:
    type: array
    default: { expr: "run.repositories" }
```

Two failure modes, detected at different times:

| Failure | Detected | Blocks |
|---|---|---|
| An **undeclared** reference: `params.foo` where `foo` is not declared | Template load | Yes |
| An **unbound** reference: a required parameter with no value | Binding, at run start | Yes |

A template is meant to have unbound parameters, so an unbound reference is a
binding-time error, not a load-time one. Either way it is caught before any
agent sees it, so a placeholder never reaches an agent as literal text.

---

## 10. Artifact contracts (L6)

An artifact contract is a typed schema for something a step produces and
another consumes. A broken artifact is then reported as a named field and rule,
not an unhelpful error.

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
| Producer side, after the step | The produced artifact is validated against the type's JSON Schema | The field-level errors go back to the agent **in the same session** for a bounded number of correction turns. Only if the artifact still fails is the outcome `failed` |
| Consumer side, at load | For each `consumes`, the producer's `produces` entry must declare the same type, or a type that declares `compatible_with` it | Blocking validation error |
| Consumer side, at step start | The artifact exists and still validates | Blocking; the step fails |

**Correction turns, not a re-run.** A missing required field rarely needs the
agent to start again; in the prototype it could usually be fixed in place. The
step returns the errors to the running session for up to
`limits.correction_turns` turns (default 2). A correction turn is **not** an
attempt: no new sandbox, no new session, and the work done so far is kept.

Compatibility is declared (`compatible_with`), not inferred from the shape of
the schemas: it is explicit, checkable, and an author's decision.

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

A role is library content: what an agent may do. `write_access` is engine
mechanism: the engine enforces it (`docs/06` section 2.2).

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

| Field | Meaning |
|---|---|
| `write_access` | `none`, `branch`, `repository`. Enforced by the sandbox and by the hook endpoint on every tool call |
| `tools` | The tool allowlist the agent runtime is given |
| `capabilities` | Queue routing (decision S1: agent steps go to task queues by capability, such as `os-windows`) and capability negotiation at run start (decision S3) |
| `runtime.requires` | Capabilities the agent runtime must declare. **Not** a product name |
| `skills` | Library references |

**No agent can push.** Agents never hold a credential that can push (DR13). An
agent's code host credential is read-only, per step, scoped to the run's
repository plus any other repositories the workflow declares the agent may read
(for example to verify a contract).

The push is part of the agent step. The engine fetches the agent's commits from
the sandbox's checkout and pushes them to the ticket's own branch with its own
credential, at the end of the step and at work-in-progress points. This is a
push, not a merge; merges are `scm.merge` (section 3).

| Rule | Detail |
|---|---|
| Push follows `write_access` | A step with `write_access: none` pushes nothing; commits made anyway fail the step (the existing after-step check) |
| `push` input on `agent.run` | Default `true`. A workflow sets `false` for scratch steps whose commits should not reach the branch |
| A failed step's commits | Pushed to a hidden work-in-progress ref, not the ticket branch, so a retry can pick them up |

### 11.0 Privilege comes from the role, never from the step

An agent may not `replace_role` (section 15.4), but it may `replace_inputs` on
an unprotected step or insert a new `agent.run`. If a step could set
`write_access` or `tools`, an agent's patch could grant itself
`write_access: repository` and any tool list. So:

> **`write_access`, `tools` and `capabilities` come only from the role.** A step
> may **tighten** `write_access`, never relax it. It may not set `tools` or
> `capabilities` at all.

| Setting | Source | A step may |
|---|---|---|
| `write_access` | The role | Tighten: `repository` to `branch`, `branch` to `none`, and so on. Never relax |
| `tools` | The role | Nothing |
| `capabilities` | The role | Nothing |

So a planner step is `role: feature-flow-default:roles/planner` with
`write_access: none`: a tightening, which is allowed and keeps the restriction
visible in the plan.

**Second control: which roles a re-plan may use.** A step could still gain
access by naming a role that has it. Organisation policy therefore has
`policy.replan.allowed_roles` (`docs/07` section 4). An agent-proposed step or
`replace_inputs` may use only a role on that list; otherwise the patch becomes a
**pending human decision** (section 15.5), not a rejection.

Validation enforces both: a step whose `write_access` exceeds its role's is a
blocking error, and an agent patch introducing a role outside `allowed_roles` is
a pending decision.

### 11.1 Runtime selection is role mapping in the organisation layer

A role does not name a model or a runtime product. Every provider type may have
several named instances with one default (`docs/07` section 3), and the
organisation layer maps each role to an instance.

```yaml
# Organisation layer (docs/07 section 4)
roleMapping:
  implementer: { agentRuntime: claude-code, model: default }
  reviewer:    { agentRuntime: copilot-cli, model: default }
```

A role that named `claude-code` would break in an installation that ships only
`copilot-cli`. Templates refer to capabilities and roles, never to names that
exist only in some installations.

The review design (`docs/19`) recommends a different, ideally stronger, model
to review than to author. That is a `roleMapping` choice and workflow policy, not
an engine rule, because other models may not be available. The engine records
which model authored and which reviewed, so the choice is visible. The reviewer
may run on any agent runtime.

### 11.2 What each layer may do to a library role

| Layer | May | May not |
|---|---|---|
| Library | Define the role | — |
| Organisation | Map the role to a runtime instance and a model; **tighten** `write_access`; **remove** tools | Relax `write_access`; **add** tools |
| Repository | Nothing | Anything |
| Run | Nothing | Anything |

Tightening is one-directional: `write_access` may only move towards `none`, and
`tools` may only shrink. Validation compares the effective role against the
library role and rejects any change in the other direction.

---

## 12. Tasks and skills (L5)

A task is the instruction an agent step is given; a skill is reusable guidance
attached to it. Both are library content.

### 12.1 References

One format, defined in `docs/11` section 3: `<package>:<type>/<name>`, with an
optional version constraint `<package>@<constraint>:<type>/<name>`.

```yaml
inputs:
  task_ref: feature-flow-default:tasks/implement-ticket
  skills: [feature-flow-default:skills/patch-review]
```

### 12.2 How parameters reach a task

A task is a Markdown document. The engine renders it before the step starts.

- The rendering language is **CEL**, as everywhere else.
- `{{ <cel> }}` marks an expression inside task text. Everything else is
  literal.
- The binding context is the step's `inputs.bindings` map plus `params`, `run`
  and `steps`.

```markdown
# Implement ticket {{ params.ticket_id }}

Repository: {{ bindings.repository }}

The acceptance criteria are in the plan artifact at:
{{ bindings.plan_path }}

Work only on the branch you are given, and commit your changes there.
Do not push: the engine pushes your commits.
```

```yaml
inputs:
  task_ref: feature-flow-default:tasks/implement-ticket
  bindings:
    repository: { expr: "run.repositories[0]" }
    plan_path: { from_artifact: "steps.plan_spec.artifacts.plan" }
```

**A consumed artifact renders as a path, not a hash.** An artifact store
reference is something the agent cannot open. A `from_artifact` binding is
copied into the sandbox by `CopyInAsync` (`docs/05` section 2, the sandbox
provider contract) and renders as the **path** the agent can read. A binding
that is genuinely a reference, which the agent should quote back but not open,
uses `expr` and renders the reference.

### 12.3 Where the rendered prompt is stored

The rendered task is written to the artifact store, content-addressed. At
execution time the step's `task_ref` points at the **rendered** artifact.

The run records both:

| Record | Value |
|---|---|
| Source | Package, version, content path, content hash |
| Rendered | Artifact hash |

So an audit can answer "what exactly did the agent read" after the package has
moved on, and a mid-run library override is auditable (`docs/11` section 6).

---

## 13. Child plans (L9)

How an epic gets one child run per ticket, when the tickets are only known once
a planning step has run.

### 13.1 The mechanism

1. A planning step (`agent.run` with a planner role) produces a **ticket-list
   artifact**: a typed artifact (section 10) listing tickets with their
   repositories and parameters.
2. `plan.replan` consumes it and returns a **patch** that adds one `run.child`
   step per ticket to a `parallel` step, each with `plan_ref` and parameters
   taken from the artifact.
3. The interpreter applies the patch through `applyPatch`. The branches are now
   ordinary steps: validated, auditable, and visible in the readable form.

**The expansion is deterministic.** Turning a typed ticket list into one
`run.child` per item is a mapping, not a judgement, so no agent is used: an
agent would add cost and a failure mode to a transformation that cannot fail if
the artifact validates. `plan.replan` takes a **template mapping**:

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

`$.<field>` is a path into each item of the artifact, not CEL: the mapping is
data and no expression is evaluated. A re-plan that needs judgement can still
use an agent (`mode: agent`); the deterministic form is the default for
expansion. Either way the result is a **patch**.

**An expansion of zero items is not an error.** A ticket list with no tickets,
or a repository list with one entry, are legitimate results of planning.

| Case | Behaviour |
|---|---|
| Zero items, `shape: parallel` | The patch adds no branches. The `parallel` step has an empty branch list and **completes immediately** with `completed` |
| Zero items, `shape: chain` (section 15.3.2) | The patch adds no steps. `target` routes directly to `on_other` |
| Zero items and no `on_other` | A **blocking validation error at patch time**: with nothing to chain, the route out of `target` is undefined |

Only the third case blocks. Treating an empty expansion as an error would make a
planning step that found nothing to do look like one that broke.

### 13.2 Why a patch and not dynamic binding

The alternative, a `parallel` step whose branches are bound from the artifact at
execution time, is rejected.

| | Patch adding `run.child` steps | Dynamic binding from an artifact |
|---|---|---|
| Validated before execution | Yes, by `applyPatch` | No; the branches do not exist at load |
| In the readable form | Yes | No |
| Auditable as a diff | Yes | No |
| Deterministic under replay | Yes | Depends on artifact content read in an activity |

Dynamic binding would also make the plan impossible to validate at load, which
breaks the validation in section 16.

### 13.3 How a child re-plan reaches the parent

A child run returns `replan_parent` with a patch for the **parent** plan. The
parent applies it through the same `applyPatch` (`docs/01` section 3.4).
Completed steps are untouched; protected steps stay intact. This is the scenario
in `docs/01` section 6.2 (epic with a mid-run re-plan), worked in `docs/13`
section 3.

**The interpreter handles it; it is not routed.** A branch must not route
`replan_parent` to an agent `replan` step: that step would re-plan from its own
inputs (`steps.plan_tickets.outcome` in the epic example) and **discard the
child's patch**, losing the discovery `replan_parent` exists to carry.

The patch has the same three possible results as any other (section 15.5):
applied, pending human decision, or rejected. **Sibling branches keep running.**

---

## 14. Policy references (L12)

A template names a **decision type**. It does not contain policy.

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
| Who may approve, minimum count, whether an agent counts | `policy.approval` | Organisation layer (`docs/07` section 4) |
| Whether it may be decided from chat | `policy.chat.allowedDecisions` | Organisation layer |
| Whether it may be auto-approved | `policy.auto_approve` | Organisation layer |
| Whether the engine may merge into the default branch | Floor F1, a configurable setting (section 14.2) | Organisation, then repository, then workflow |

Approval rules such as `required_role` or `min_approvers` are not gate inputs.
In a template they would let the template relax an organisation's approval rule.

`decision_type` values are namespaced strings (`ticket.merge_approval`,
`epic.delivery_approval`, `delivery.partial_approval`). The organisation's
`policy.approval` is keyed by them. An unknown decision type is a blocking
validation error at run start.

### 14.1 A gate may record a choice, not only approve or reject

A partial delivery offers a human three choices: retry, fix forward, revert
(`docs/01` section 7.3). Three new outcomes for one step would bloat the
vocabulary. Instead:

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

The choice is visible in the audit record and in the readable form.

### 14.2 Engine merges into the default branch (floor F1)

Floor F1 is now a **configurable setting with a safe default**, not a fixed
floor. The ID F1 is kept so existing references resolve.

| Rule | Detail |
|---|---|
| Default | The engine may not merge into the default branch, so `scm.merge` there must have `engine_can_merge: false`: the engine raises a human decision request, a human merges on the code host, and the engine detects the merge and continues |
| Who sets it | Hierarchically: organisation, then repository, then workflow. It may be relaxed, for example for a proof-of-concept repository. The organisation can lock it so lower levels cannot relax it |
| What can never change it | Anything an agent can edit. The repository setting is read from the repository's configuration **on its default branch**, never from the ticket branch, and no plan patch during a run can change it |
| Enforcement | At plan validation, and again at merge time, reading the default branch from the code host (DR2) |
| Final fallback | Code host branch protection on `main` (requiring independent review). The engine never holds a bypass |

A template naming a decision type for the merge gate (section 14) is unchanged:
when the engine may not merge, the human decision request is that gate.

---

## 15. Patches

A patch is how a plan changes during a run, whether proposed by an agent
(re-plan) or applied by a human (steering).

### 15.1 The patch document

A patch is its own document, not part of the plan.

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
not the current revision is rejected as stale and must be re-proposed against
the current one. It is never silently rebased.

### 15.1.1 The patch names its resume point

A patch says where the run continues once it is applied, because only the patch
knows whether it inserted a step before the failing one or fixed the plan
somewhere else.

```yaml
base_revision: 7
resume_at: implement
changes: [ ... ]
```

| Rule | Detail |
|---|---|
| `resume_at` | A step id, validated like a route target: it must exist after the patch is applied |
| Meaning | `plan.replan`'s `replan_requested` route means "continue at the patch's resume point", not "go to a fixed step" |
| Default if omitted | The step whose outcome routed the run to `plan.replan`. The common case: a step fails, asks for a re-plan, and is retried |
| Validation | A `resume_at` that does not exist after the patch is a blocking error |

So every `plan.replan` step routes `replan_requested: { resume: true }`
(section 6.2), and the interpreter resolves the target from the patch. A plan
author does not have to guess where a re-plan will land. `{ resume: true }` is
the only form.

### 15.2 "Weaken" is defined

Floor F3 says a protected step cannot be removed or weakened by a re-plan or by
steering. "Weakened" means any of:

| Operation on a protected step | Blocked |
|---|---|
| Removing it | Yes |
| **Bypassing it**: any change that makes it unreachable from the plan's entry point | Yes |
| Changing its `inputs` | Yes |
| Changing any route **out** of it | Yes |
| Changing its `limits` | Yes |
| Changing its role | Yes |

Bypassing matters because `change_route` elsewhere could make a protected step
unreachable without touching it. Reachability is therefore a validation check
(section 16.3).

### 15.3 Operations

Eleven operations.

| Op | Effect |
|---|---|
| `insert_before` | Insert a step before `target`. Section 15.3.1 |
| `insert_after` | Insert a step after `target`. Section 15.3.1 |
| `replace_step` | Replace the step at `target` |
| `remove_step` | Remove the step at `target` |
| `replace_inputs` | Replace the `inputs` of `target` |
| `replace_limits` | Replace the `limits` of `target`, within ceilings (floor F4) |
| `replace_role` | Replace the role of `target` |
| `change_route` | Change one route of `target` |
| `add_branch` | Add a branch to a `parallel` at `target` |
| `remove_branch` | Remove a branch from a `parallel` at `target` |
| `add_chain` | Add a step chained after `target`: `target`'s `completed` route goes to the new step, every other outcome goes to `on_other`. Section 15.3.2 |

There is no `set_parameter` (parameter binding covers it), no
`override_library_reference` (mid-run override covers it, section 15.6) and no
`insert_policy_guard`.

### 15.3.1 `insert_before` and `insert_after` are defined

On a graph, "insert after X" is ambiguous unless it says which routes are
rewired. The rule:

> `insert_after X` inserts `S` immediately after `X`. Every route of `X` that
> pointed at some step `Y` now points at `S`. `S`'s own routes are given
> explicitly in the patch. `insert_before X` is the mirror: every route from any
> step that pointed at `X` now points at `S`, and `S` routes to `X`.

Why keep these rather than only `add_step` plus `change_route`:

| | `insert_after` with rewiring | `add_step` plus `change_route` |
|---|---|---|
| Operations to insert into a chain | 1 | 2, and the author must know every inbound route |
| Failure mode if the author forgets | None; rewiring is automatic | A dangling route, or a step that is added but unreachable |
| What an LLM must get right | The step and its own routes | The step, its routes, **and** every route that must be re-pointed |

An LLM writing a patch knows where it wants the new step. It does not reliably
know every inbound route to `X`. So `insert_after` followed by `change_route` on
the same target is **redundant**; `docs/13` section 3 uses `insert_after` alone.

**Measured in Spike 2** (`docs/16` section 3): a planner model authored 20
patches in each form. Both forms: 0% planner failures. Validation refused 4 of
20 `insert_after` patches and 5 of 20 two-operation patches, all because the
planner chose a target whose rewiring stranded a protected step (floor F3). A
one-trial gap is too small to call either form safer; the argument above stands
on its own.

### 15.3.2 Ordered work is a chain, not a concurrency limit

Ordered delivery to two repositories is **not** a `parallel` with
`max_concurrency: 1`:

- With `join: all`, if the first repository fails the second still runs,
  because the join waits for every branch.
- "In the order added" would be an implicit property of a concurrency limit, not
  a declared dependency.

**`mode: expand` has a `chain` shape.** Order is a declared dependency,
expressed as routes.

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

This produces one `add_chain` per item:

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

`add_chain` points `target`'s `completed` route at the new step and every
**other** outcome of `target` at `on_other`. So `deliver_api` completing goes to
`deliver_web`; `deliver_api` failing goes to `delivery_gate`, and `deliver_web`
never runs. The ordering is visible in the plan rather than implied by a number.

Rejected: `order: sequential` with `stop_on: any_failed` on `parallel`. It keeps
a fan-out shape for work that is not fan-out, and needs a second mechanism to
say what a route already says.

### 15.4 Who may use which operation

| Op | An agent may propose | A human may apply |
|---|---|---|
| `insert_before`, `insert_after` | Yes | Yes |
| `replace_step` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `remove_step` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `replace_inputs` | Yes; if `target` is protected, or the role is outside `policy.replan.allowed_roles`, a **pending human decision** | Yes |
| `replace_limits` | Yes, within `policy.attempts.ceiling` | Yes, up to `hardCeiling`; beyond that needs the admin role (floor F4) |
| `replace_role` | **No** | Yes, `steerer` or `admin` |
| `change_route` | Yes; if `target` is protected, a **pending human decision** | Yes |
| `add_branch` | Yes, if the `parallel` has not started | Yes |
| `remove_branch` | Yes, if the branch is not protected and has not started | Yes |
| `add_chain` | Yes, if `target` has not started | Yes |

"Pending human decision" is not a rejection: a human may approve it with a
reason (section 15.5). Only a patch that would relax a safety floor is rejected
outright.

### 15.5 A patch touching protected content becomes a human decision

Decided by the product owner, 2026-10-27, because in the prototype a re-plan
silently changed a ratified detail.

- An **agent** patch that touches protected content is **not rejected**. It
  becomes a **pending human decision**, recorded with the diff and the reason.
- A **human** may approve it with a reason. The approval is audited like any
  steering action.
- **Safety floors stay absolute against patches.** No patch, human or agent,
  relaxes floors F1 to F4 (`docs/07` section 2: whether the engine may merge
  into the default branch, secrets by reference, protected steps, attempt
  ceilings). F1 is configurable, but only through configuration (section 14.2),
  never by a patch. A patch that would relax a floor is rejected outright, with
  no human override.

So a patch has three possible results: applied, pending human decision,
rejected.

### 15.6 Mid-run library override is not a plan patch

Overriding library content for a running run is a **separate audited operation**
(`docs/11` section 6), not a patch operation. It changes what content future
steps resolve; it does not change the plan. Kept separate, the plan revision
history is a record of *structure* and the override log a record of *content*.

---

## 16. Validation

The full table of checks is in `docs/12-validation-and-testing.md`. This section
states the rule and the checks this language depends on.

### 16.1 The blocking rule

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

Warnings are listed in the run start record. They never change behaviour, and a
template cannot register a warning of its own at run time: warnings are static
only.

### 16.2 Phases

| Phase | Blocks on |
|---|---|
| Template load | Schema, undeclared references, unknown kinds, duplicate ids, expression syntax and types, contract compatibility, unrouted outcomes, cycles without an exit, unreachable steps |
| Binding (run start) | Unbound required parameters, library resolution, creating the library lock (the versions pinned for the run, `docs/11` section 4.2), capability negotiation, policy eligibility, unknown decision types |
| Patch | Everything in load, plus protection invariants, `base_revision` staleness, and the operation's authorisation |
| Step start | Inputs satisfy the kind's contract; consumed artifacts exist and validate |

Attempt ceilings are not a validation phase: the interpreter checks them on
every execution of a step (section 6.3).

### 16.3 Checks this language depends on

| Check | Blocks |
|---|---|
| Unreachable steps | Yes |
| Every outcome the kind can return is routed | Yes |
| Every cycle has a route out | Yes |
| Producer and consumer contract compatibility | Yes |
| Expression type check | Yes |
| **Protected-step reachability**: no patch may make a protected step unreachable | Yes, on patch |

---

## 17. Changes this document makes to engine documents

Where the language needed an engine document to change, the change was made in
that document:

| Document | Change | Why |
|---|---|---|
| `docs/06` section 3.1, `human.gate` | Inputs `required_role` and `min_approvers` removed; `gate.decision_type` added | Approval policy is organisation configuration, not template content (L12, section 14) |
| `docs/06` section 3.2, `human.session` | `session_id` removed (the engine generates it); `checks` replaced by `checks_from` (step ids whose validators to offer) | So the checks offered in a session are the same ones the run will apply later; no check appears for the first time after submission |
| `docs/06` section 6.5, `scm.merge` | `method` removed; `require_human` removed (now `engine_can_merge` and `squash`, section 3) | The organisation allows merge methods and the repository chooses one; whether the engine may merge into the default branch is floor F1, a setting (section 14.2), not a free input |
| `docs/06` section 7.4, `test.run_tiers` | `tiers` replaced by `tier_tags`; `execution_target` removed | Tiers come from `feature-flow.yaml`, read per step |
| `docs/06` section 10.1, `fix.verify` | `regression_test_required` removed | A regression test for every fix is a step invariant, not a template switch |
| `docs/08` | Spike 1 gained a worker slot capacity measurement; the Temporal-unavailable injection states that workers re-attach by handle; the `wait.for_state` poll form is stated | Gaps in the spike plan found while designing the language |

---

## 18. Open questions

**Answered:**

| Question | Answer |
|---|---|
| Is a hand-written CEL subset an acceptable fallback? | Yes (product owner, 2026-10-27). Not needed: Cel.NET passed every check in Spike 1 (section 8.1) |
| How does a partial delivery offer three choices? | A gate keeps `approved` / `rejected` and records a `choice`; guarded routes select on it (section 14.1) |
| Artifact compatibility: declared or structural? | Declared, with `compatible_with` (section 10.3) |
| `run.wait` or `parallel`? | `parallel`. `run.wait` is out of the version 1 language and reserved in `docs/06` (section 3) |
| Per-kind input schemas: generated or hand-written? | Generated, as an implementation task |
| Is the `outputs` cap of 16 entries and 256 bytes right? | Kept. Spike 2 measured at most 5 entries and 71 bytes (section 8.3.2) |
| Is `insert_after` with rewiring easier for a planner than `add_step` plus `change_route`? | Spike 2 found no meaningful difference: 0% planner failures with both. `insert_after` stays (section 15.3.1) |
| Which outcome does `scm.merge` return when the target branch moved since catch-up? | `base_moved`, routed back to catch-up; the merge step's attempt allowance (default 3) caps the refusals (sections 4.2, 6.3) |
| What status does a run report when it stops without reaching an `end:` route? | `incomplete`; a parent's `run.child` sees it as `failed` (section 6.2) |
| When a loop re-runs a step, what happens to its earlier results? | Outcome, outputs and artifacts are cleared; the previous execution stays readable as `steps.<id>.previous`, with `steps.<id>.executions` counting executions (sections 6.3, 8.3) |

**Open:**

| Question | Why it is open |
|---|---|
| How a child run's `{ end: partial }` or `{ end: rejected }` maps to a `run.child` outcome | `run.child` returns `completed`, `failed`, `replan_parent` or `attempts_exhausted`; the mapping from those child run statuses is not stated |
