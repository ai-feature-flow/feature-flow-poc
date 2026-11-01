# Workflow language

Status: draft, 2026-10-31.

This document owns the language templates and plans are written in: the
document shape, steps, outcomes, routing, expressions, parameters, artifact
contracts, parallel work, child runs, patches and fragments. Terms are defined
in `docs/01-concepts.md`. Each engine action's inputs and outcomes are owned by
`docs/04-engine-mechanisms.md`; this document names actions only in examples.

All keys, and the names of steps, artifacts and library content, are snake_case (RW12).

---

## 1. Document shape

A template and a plan are the same document; a plan has its parameters bound
(`docs/01` section 2).

```yaml
api_version: feature-flow/v1
kind: template                  # template | plan
metadata:
  name: ticket_delivery
  level: ticket                 # the workflow's name for this level
  description: One ticket, from plan to merge into its parent branch

parameters:                     # section 6
  ticket_id: { type: string, required: true }

artifact_types:                 # library types this workflow uses (section 7)
  - std:artifact_types/delivery_plan

defaults:                       # section 4.3 and section 2.1
  routes:
    by_class: { failure: { escalate: true }, blocked: { escalate: true } }
  limits: { attempts: 3 }

steps:                          # an ordered list (section 2)
  - ...
```

| Key | Required | Meaning |
|---|---|---|
| `api_version` | Yes | The plan schema version. The interpreter and the library are versioned separately |
| `kind` | Yes | `template` or `plan` |
| `metadata.level` | Yes | The level a run of this template is at. Free name |
| `metadata.branch_pattern` | No | How this run's work branches are named, for example `ff/{{ run.level }}/{{ params.ticket_id }}`. Default `ff/<level>/<run id>` |
| `parameters` | No | Section 6 |
| `artifact_types` | No | Library references. A step may use only types listed here or in a fragment it uses |
| `defaults` | No | Route defaults and limits for every step, overridable per step |
| `budget.max_work_items` | No | Root templates: the most descendant runs the tree may create, within the organisation's ceiling (F11) |
| `steps` | Yes | The ordered step list |

---

## 2. Steps

### 2.1 Common fields

A step has exactly one of the keys `agent`, `decision` or `action`, which sets
its form (`docs/01` section 2.1). A list entry may instead be `parallel`,
`for_each` (section 8) or `use` (section 11).

| Field | Forms | Meaning |
|---|---|---|
| `id` | All | snake_case, unique in the run, never reused for a different step. Must not contain a `.` (reserved for fragment ids) |
| `name` | All | Label for humans. Optional |
| `agent` / `decision` / `action` | One of them | The form, and its form-specific settings (sections 2.2 to 2.4) |
| `with` | All | Inputs. For an agent step, the bindings its task is rendered with; for an action, the action's inputs; for a decision, its prompt bindings. Values are literals or expressions (section 5) |
| `outcomes` | Agent, decision | Declared outcomes and their classes (section 3). Actions have fixed outcomes |
| `routes` | All | Section 4 |
| `produces`, `consumes` | All | Section 7. A decision produces artifacts only through an interactive session |
| `verdicts`, `ratifies` | Agent, decision | Section 7.4 |
| `outputs` | All | Small typed values the step publishes for routing (section 5.4) |
| `limits` | All | `attempts`, `timeout`, `correction_turns`, for agents `questions` (section 2.2), and for `scm.merge` `races` (`docs/04` section 7.3; LOG 61, unreviewed). Each has an engine default, overridable by `defaults.limits` and the step, within organisation ceilings |
| `repository` | All | The repository the step works on, when the run has several. An expression or a name from the run's repositories. Default: the run's first repository |
| `protected` | All | `docs/01` section 2.3 |
| `tracker` | All | Declarative tracker updates when the step starts or ends with an outcome (section 2.5) |

### 2.2 Agent steps

```yaml
- id: implement
  agent:
    role: std:roles/implementer
    task: std:tasks/implement_ticket
    skills: [std:skills/dotnet_conventions]
    write_access: branch          # optional; may only tighten the role's value
    context: full                 # full | none: whether earlier steps' summaries are shown
    planning:                     # optional: authority to propose patches (section 10)
      targets: [self]             # self | parent
      templates: [std:templates/ticket_delivery]
  with:
    plan_path: { from_artifact: plan }
  outcomes: { done: success, blocked: blocked, needs_replan: blocked }
  limits:
    attempts: 3
    timeout: 4h
    questions: { max: 3, timeout: 4h }   # ask_human (RW14); absent: the tool is not offered
```

| Field | Meaning |
|---|---|
| `role` | Library role: write access, tools, toolsets, runtime capabilities (`docs/01` section 2.2) |
| `task` | Library task, rendered with `with` (section 6.3) |
| `skills` | Library skills, mounted for the agent to read on demand. Added to the role's |
| `write_access` | `none`, `branch` or `repository`. Tightens only |
| `context` | `full` (default): the agent is given the run's step summaries. `none`: it is not, so a reviewer judges independently (from FF) |
| `planning` | Lets the agent propose patches to this plan (`self`) or the parent's (`parent`), and names the templates it may start child runs from. `review: required` makes every proposal a patch approval decision; default `auto` (`docs/05` section 6) |
| `push` | Default `true`. `false`: the agent's commits are not pushed (scratch work) |
| `on_uncommitted` | `commit` (default) or `fail`: what to do with changes left uncommitted at the end |

### 2.3 Decisions

```yaml
- id: approve_spec
  decision:
    type: ticket.spec_approval          # declared in the library (docs/05)
    prompt: std:prompts/spec_approval
    auto: approved                      # requested auto-resolution, within organisation policy
  consumes: [{ name: spec }]
  ratifies: { approved: [spec] }
  outcomes: { approved: success, rejected: failure, needs_research: blocked }
```

| Field | Meaning |
|---|---|
| `type` | The decision type. Who may answer comes from organisation policy for that type |
| `prompt` | Library prompt, rendered with `with` |
| `auto` | The outcome the template asks policy to choose without a human. Applied only if organisation policy allows auto-resolution for this type (`docs/05`) |
| `session` | Optional: open an interactive session (`docs/01` section 7.1). Names the workspace repository and folder, the files the person writes and their artifact types, and `checks_from`: the later steps whose checks the person may run before submitting |

### 2.4 Engine actions

```yaml
- id: run_tests
  action: test.run
  with: { tier_tags: [unit, component] }
```

An action's inputs, outcomes and outcome classes are fixed by its contract in
`docs/04`. A step may route them but not declare others.

### 2.5 Tracker updates

Any step may move the run's tracker issue, from FF:

```yaml
tracker:
  on_start: in_progress
  on: { done: code_review, rejected: in_progress }
```

Values are status names from the library's status map, which the organisation
may override, so a template does not hard-code one tracker's statuses. A failed
tracker update is an infrastructure failure: retried, never a step failure.
More complex tracker work (creating tickets, links, comments) is the
`tracker.update` action.

---

## 3. Outcomes

```yaml
outcomes: { accepted: success, rejected: failure, unsure: ~ }
```

- A map from outcome name to class (`success`, `failure`, `blocked`) or `~`
  (no class). Names start with a letter and use letters, digits and `_`.
- An agent or decision step must declare at least one outcome.
- The engine adds `attempts_exhausted` (class `failure`) to every step that
  consumes attempts (`docs/01` section 3.2). It may not be declared.
- Actions have fixed outcomes (`docs/04`). A decision's answer must be one of
  its declared outcomes.

---

## 4. Routing

### 4.1 Route targets

```yaml
routes:
  done: run_tests                                  # a step id
  rejected: { to: implement, clean_up: true }      # a step id with options
  needs_research: { escalate: true }               # an intervention
  abandoned: { end: rejected }                     # end the run
  failed:                                          # guarded
    - when: "steps.review.outputs.blocking > 0"
      to: fix
    - to: { escalate: true }
```

| Target | Meaning |
|---|---|
| `<step id>` or `{ to: <id> }` | Run that step next. Re-entering a step runs it again (`docs/01` section 3.3) |
| `{ end: <status> }` | `completed`, `failed`, `rejected` or `partial` |
| `{ escalate: true }` | Raise an intervention. Optional `type` (a decision type, default `<level>.escalation`) and `prompt` |
| Guarded list | Conditions in order; the first true one wins; the last entry has no `when` |
| `{ exit: <name> }` | Inside a fragment only: leave the fragment by a named exit (section 11) |

`clean_up: true` on a `to` route resets the work branch to the commit the target
step started from, before the target runs. Default `false`. A human answering an
intervention may choose it either way.

### 4.2 Re-entry

When a route enters a step that has run before, the step runs again. Its
current outcome, outputs and artifacts are cleared. The earlier execution stays
readable as `steps.<id>.previous`, and `steps.<id>.executions` counts
executions. The entry counts against the step's attempt allowance (`docs/01`
section 4).

### 4.3 Route resolution

For each outcome of each step, the route is the first of:

1. The step's own `routes` entry for that outcome.
2. `defaults.routes.by_outcome` for that outcome name.
3. `defaults.routes.by_class` for the outcome's class.
4. For class `success`: the next step in the list, or `{ end: completed }` after
   the last step.
5. For `attempts_exhausted`: `{ escalate: true }` (the engine default).

An outcome left with no route is a blocking validation error. Validation and
the readable form show the resolved routes, so every route is visible even
where the author wrote none.

Branch steps inside `parallel` do not route; section 8.1.

---

## 5. Expressions

### 5.1 The language

Expressions are written in CEL (Common Expression Language) and evaluated by
Cel.NET. CEL is side-effect free, has no loops or I/O, and has a static type
checker, so every expression is type-checked when the template loads. Spike 1
verified Cel.NET's restriction, determinism under replay, cost estimation and
speed (`archive/docs/15` section 9.2).

| Place | Form |
|---|---|
| A route guard | `when: "<cel>"` |
| An input value | `{ expr: "<cel>" }` |
| A parameter default | `default: { expr: "<cel>" }` |
| Task and prompt text | `{{ <cel> }}` |

Anywhere else a string is a literal.

### 5.2 Allowed functions and bounds

Predicates, selection and arithmetic only: `size`, `contains`, `all`, `any`,
`exists`, `startsWith`, `endsWith`, `lower`, `upper` (registered by the engine,
culture-invariant), comparison and boolean operators. No comprehensions, no
regular expressions.

| Bound | Value |
|---|---|
| Length | 1,000 characters |
| Syntax tree | 200 nodes |
| Cost | CEL's static cost estimate, capped. Cel.NET cannot interrupt a running expression, so the estimate is the stop |

The environment is built with an explicit allow-list, never Cel.NET's default
environment, which registers more than this list.

### 5.3 Namespaces

| Namespace | Contents |
|---|---|
| `params` | Bound parameters |
| `steps.<id>` | `outcome`, `outputs.<name>`, `artifacts.<name>` (a reference, never content), `previous`, `executions` |
| `run` | `id`, `level`, `repositories`, `branch` (this run's work branch, per repository: `run.branch['acme/api']`), `parent_branch` (likewise), `tracker_issue`, `parent_id` |
| `findings` | Counts from the ledger for this run and its children: `open`, `open_blocking`, `open_demonstrated`, `fixed`, `declined`, `escalated`, `reraised_unchanged`, `repeat_fixes` (findings reopened after being fixed at least twice) |
| `item` | Inside `for_each` only: the current item (section 8.2) |

**Expressions never read artifact content or call providers.** That would be
I/O in workflow code and would break deterministic replay. A value that must
reach routing is published as an output by the step that has it.

### 5.4 Outputs

A step declares small typed values it publishes, for routes and later inputs:

```yaml
outputs:
  tier_tags: { type: array }
  blocking: { type: number }
```

| Rule | Value |
|---|---|
| Types | `string`, `number`, `boolean`, and lists of those |
| Size | 16 entries, 256 bytes each. Spike 2 measured at most 5 entries and 71 bytes (`archive/docs/16` section 2) |
| Written by | The step's activity: an action's contract, or an agent through its reporting contract (`docs/04`). Never by workflow code |

**Repository facts reach routing through outputs.** The `repo.inspect` action
reads the repository manifest and publishes facts such as which test tiers
exist or whether CI is configured. The manifest is still read fresh at each
step (RW11); `repo.inspect` publishes what it saw at that moment, with the
manifest hash.

---

## 6. Parameters and bindings

### 6.1 Declaration

```yaml
parameters:
  ticket_id: { type: string, required: true, description: "Tracker key" }
  repositories: { type: array, default: { expr: "run.repositories" } }
  epic_contract: { type: artifact, required: false }
```

Types: `string`, `number`, `boolean`, `array`, `object`, and `artifact` (a
reference to an artifact, resolved when consumed).

### 6.2 Where a value comes from

Lowest precedence first, from FF's input precedence
(`../feature-factory/docs/design/engine-flexibility.md` item 1):

1. The template's default.
2. The library manifest's `inputs`, then the organisation's library inputs.
3. For a child run: the parent's resolved parameters, **for names the child
   declares**.
4. The run request, or the `with.parameters` of the `run.child` step that
   started the child.

Engine values (`run.*`) cannot be declared as parameters. A reference to an
undeclared parameter fails at load; a required parameter left unbound fails at
run start, so a placeholder never reaches an agent as literal text.

### 6.3 Rendering tasks and prompts

A task or prompt is Markdown. `{{ <cel> }}` marks an expression; the binding
context is the step's `with` plus `params`, `run` and `steps`. A `with` value
written as `{ from_artifact: <name> }` copies that consumed artifact into the
sandbox and renders as its **path**, so the agent can open it. The rendered text
is stored as an artifact and its hash recorded with the attempt (P7).

---

## 7. Artifact contracts

### 7.1 Producing

```yaml
produces:
  done: [{ name: plan, type: std:artifact_types/delivery_plan }]
```

Per outcome: the artifacts the step must produce when it ends with that
outcome. A list instead of a map applies to every outcome of class `success`.
An agent that reports an outcome without producing what it promised, or
produces something not declared, fails validation of its output and gets
correction turns (`docs/01` section 5.4).

An agent or decision step may not declare an evidence type (`docs/01` section
5.2).

### 7.2 Consuming

```yaml
consumes:
  - { name: plan }                                  # this run
  - { name: epic_acceptance_criteria, from: parent }
  - { name: findings_report, from: { child: ticket_a } }
  - { name: previous_review, optional: true }
```

| Field | Meaning |
|---|---|
| `name` | The artifact's logical name |
| `from` | `current` (default), `parent`, `root`, or `{ child: <step id> }` |
| `optional` | The step runs even if the artifact does not exist yet |

Each consumed artifact is pinned at its revision current when the attempt
starts, and staged read-only in the sandbox with a manifest listing them (from
FF).

### 7.3 Compatibility

For each consumed artifact produced in the same run, the producer must declare
the same type, or a type that declares `compatible_with` the consumer's. This is
checked at load.

### 7.4 Verdicts and ratification

```yaml
verdicts: { accepted: { accept: [implementation_claims] }, rejected: { reject: [implementation_claims] } }
ratifies: { approved: [spec] }
```

Both name consumed artifacts and record, per outcome, against their exact
revisions (`docs/01` section 5.3). `ratifies` is allowed on decisions only.

---

## 8. Parallel work, chains and fan-out

### 8.1 `parallel`

```yaml
- id: tickets
  parallel:
    branches: []                 # filled by plan.expand (section 10.4), or written out
    join: all                    # all | any | <n>
    max_concurrency: 2
  routes: { completed: epic_review, any_failed: { escalate: true } }
```

| Rule | Detail |
|---|---|
| Branches | Each branch is one step, usually a `run.child`. Branches have no routes: their outcome class is reported to the join |
| Join `all` | Completes when every branch has ended. `completed` if every branch's outcome is class `success`, otherwise `any_failed` |
| Join `any` | Completes when the first branch ends with class `success`; the others are cancelled |
| Join `<n>` | Completes when `n` branches end with class `success` |
| Concurrency | A waiting branch starts as soon as a running one ends (Spike 3, `archive/docs/18` section 4) |
| Failure isolation | A failed branch does not cancel or stall its siblings |
| Outcomes | `completed` (class `success`) and `any_failed` (class `failure`) |

### 8.2 `for_each`

Runs a step once per item of a list known when the plan is bound: a parameter,
or a run value such as the run's repositories (review finding 6.4, one
repository per ticket).

```yaml
- id: test_each_repo
  for_each:
    items: { expr: "run.repositories" }
    mode: parallel               # parallel | chain
    max_concurrency: 3
    step:
      action: test.run
      repository: { expr: "item" }
      with: { tier_tags: [unit] }
  routes: { completed: review, any_failed: fix }
```

- `step` may be one step, a `use` of a fragment, or a list of steps. Each item
  becomes one branch of the expansion; for a fragment or a list, the branch
  ends at the fragment's exit or the list's last step, and that outcome's class
  is what the join sees.
- At binding, `for_each` expands into a `parallel` (`mode: parallel`, join
  `all`) or a chain (`mode: chain`), with step ids `<id>_<index>`, and inner ids
  `<id>_<index>.<inner id>`. The plan,
  validation and the readable form see the expanded steps.
- A **chain** runs the items in order. Each item's `success` goes to the next;
  any other outcome ends the chain with `any_failed`, and later items do not run.
  This is how ordered delivery across repositories is written.
- A list that is known only at run time (from an artifact) is expanded by the
  `plan.expand` action (section 10.4), which produces the same shapes as a patch.

---

## 9. Child runs

### 9.1 Starting a child

```yaml
- id: ticket_a
  action: run.child
  with:
    template: std:templates/ticket_delivery
    level: ticket
    repositories: ["acme/api"]
    parameters: { ticket_id: "EPIC-101", epic_contract: { expr: "steps.design.artifacts.contract" } }
```

The child runs on the same interpreter, with its own plan. Its work branch in
each repository is taken from this run's work branch, which becomes the child's
`run.parent_branch`.

### 9.2 What a child receives

| From the parent | How |
|---|---|
| Parameters | `with.parameters`, then the parent's resolved parameters for names the child declares (section 6.2) |
| Artifacts | The child's steps consume with `from: parent` or `from: root`, pinned at the revision current when the child starts |
| Ratifications | Inherited: a parent's ratification of an artifact is authority in the child (`docs/01` section 5.3) |
| The parent branch | `run.parent_branch` |
| Findings | The parent's view of the ledger includes the child's, not the reverse |

### 9.3 What the parent sees

`run.child`'s outcome follows the child's run status:

| Child status | `run.child` outcome | Class |
|---|---|---|
| `completed` | `completed` | `success` |
| `partial` | `partial` | `failure` |
| `rejected` | `rejected` | `failure` |
| `failed`, `incomplete`, `aborted` | `failed` | `failure` |

The parent may also consume the child's artifacts (`from: { child: <id> }`).

### 9.4 A child proposing a change to its parent

A child agent step with `planning.targets: [parent]` may propose a patch to the
parent's plan, for example "add a ticket for the platform repository". It is
sent to the parent as a signal while the child **keeps running, or waits** (RW6).
The parent applies it through the same path as any patch (section 10). Sibling
branches are untouched. In a `parallel`, the child's branch counts towards the
join only when the child ends. If the child must wait for the new work, its own
plan routes to a decision or a `wait.condition` step.

---

## 10. Patches

A patch changes a running plan's structure (`docs/01` section 10). Every patch,
from any source, goes through one function that validates and applies it.

### 10.1 The patch document

```yaml
api_version: feature-flow/v1
kind: patch
metadata:
  run_id: run-1234
  proposed_by: { type: agent, step_id: plan_tickets }
  reason: "implementation found a second repository"
base_revision: 4
resume_at: tickets
changes:
  - op: add_branch
    target: tickets
    step: { id: ticket_d, action: run.child, with: { ... } }
```

| Field | Rule |
|---|---|
| `base_revision` | Optimistic concurrency. A patch against a revision that is not current is rejected and must be re-proposed; it is never silently rebased |
| `resume_at` | Required. The step the run continues at once the patch is applied. It must exist after the patch. A patch sent to a parent may omit it: the parent continues where it was |

### 10.2 Operations

| Op | Effect |
|---|---|
| `insert` | Insert a step `before` or `after` a target. `after X`: every route of X that pointed at Y now points at the new step; the new step's routes are given. `before X`: every route that pointed at X now points at the new step, which routes to X |
| `replace` | Replace the step at the target |
| `remove` | Remove the step at the target. Routes that pointed at it must be given new targets in the same patch |
| `set` | Change one field of the target: `with`, `limits`, `outputs`, `consumes`, `produces`, or `routes.<outcome>` |
| `add_branch`, `remove_branch` | Add or remove a branch of a `parallel` that has not started |
| `chain` | Add a step after the target: the target's `success` routes go to the new step, every other outcome to `on_other`, a step id given in the operation |

There is no operation to change a step's role: privilege comes from the role
(P8). A human changes a role through an override (`docs/05`).

### 10.3 Results

| Result | When |
|---|---|
| Applied | Valid, and touches nothing protected, or comes from a human with the right role |
| Pending decision | An agent's patch that touches a protected step (removes it, changes it, changes a route out of it, or makes it unreachable), or adds a step whose role is outside the organisation's allowed list. A human approves or rejects it (`docs/01` section 7.1) |
| Rejected | Invalid, stale, or it would relax a safety floor. Recorded, never silently dropped. An agent's invalid patch goes back to it for correction turns first |

Validation of a patch runs every load check on the plan as it will be after the
patch, plus the protection checks above.

### 10.4 Deterministic expansion

A list produced at run time, such as the tickets a planner wrote, becomes steps
through the `plan.expand` action. It maps each item of a list artifact to a step
(usually a `run.child`) and produces a patch adding them as `parallel` branches
or as a chain. No agent is involved: the mapping is data, so the expansion
cannot fail if the artifact is valid. An empty list is not an error: a
`parallel` with no branches completes at once.

---

## 11. Fragments

A **fragment** is a reusable group of steps from the library, for example the
standard review, the fix loop, or catch-up and merge (RW5).

```yaml
- id: review
  use: std:fragments/standard_review
  with:
    spec_refs: { expr: "[steps.plan_spec.artifacts.plan]" }
    angles: [correctness, tests, security]
  routes: { reviewed: open_pr }
  protected: true
```

| Rule | Detail |
|---|---|
| Expansion | At load, the `use` entry is replaced by the fragment's steps, with ids prefixed `<id>.`, for example `review.run_review`. Validation, the plan and the readable form see the expanded steps; the readable form also shows them grouped |
| Parameters | A fragment declares parameters like a template; `with` binds them |
| Exits | A fragment's steps may route to `{ exit: <name> }`. The `use` entry routes each exit like an outcome. Exits carry a class for default routing |
| Protection | `protected: true` on `use` protects every step of the fragment |
| Nesting | A fragment may use other fragments, to a depth of 3 |

---

## 12. Example: a ticket

The standard single-ticket template, written with defaults and fragments. It
replaces the first design's 270-line example (`archive/docs/13` section 1).
It is illustrative: the shipped `std` template uses the `plan_and_validate`
fragment where this example has an inline planner (`docs/08` section 6.4).

```yaml
api_version: feature-flow/v1
kind: template
metadata: { name: ticket_delivery, level: ticket }

parameters:
  ticket_id: { type: string, required: true }
  epic_contract: { type: artifact, required: false }
  engine_can_merge: { type: boolean, default: false }   # an epic passes true for its tickets

artifact_types: [std:artifact_types/delivery_plan, std:artifact_types/evidence_claims]

defaults:
  routes:
    by_class: { failure: { escalate: true }, blocked: { escalate: true } }

steps:
  - id: baseline
    use: std:fragments/baseline            # baseline tests and analysis on the unchanged branch

  - id: plan_spec
    agent: { role: std:roles/planner, task: std:tasks/plan_ticket }
    with: { ticket_id: { expr: "params.ticket_id" } }
    consumes: [{ name: epic_acceptance_criteria, from: parent, optional: true }]
    produces: [{ name: plan, type: std:artifact_types/delivery_plan }]
    outputs: { tier_tags: { type: array } }
    outcomes: { planned: success, blocked: blocked }
    tracker: { on_start: in_progress }

  - id: approve_spec
    decision: { type: ticket.spec_approval, prompt: std:prompts/spec_approval, auto: approved }
    consumes: [{ name: plan }]
    ratifies: { approved: [plan] }
    outcomes: { approved: success, rejected: failure }
    routes: { rejected: { to: plan_spec, clean_up: true } }

  - id: implement
    agent: { role: std:roles/implementer, task: std:tasks/implement_ticket }
    with: { plan_path: { from_artifact: plan } }
    consumes: [{ name: plan }]
    produces: [{ name: implementation_claims, type: std:artifact_types/evidence_claims }]
    outcomes: { done: success, blocked: blocked }
    limits: { questions: { max: 3, timeout: 4h } }

  - id: verify
    use: std:fragments/verify_and_review   # tests, analysis, trace, review, fix loop
    with:
      tier_tags: { expr: "steps.plan_spec.outputs.tier_tags" }
      spec_refs: { expr: "[steps.plan_spec.artifacts.plan, params.epic_contract]" }
      fixer_task: std:tasks/fix_findings
    protected: true
    routes: { ready: deliver }

  - id: deliver
    use: std:fragments/merge_into_parent   # pull request, checks, catch up, re-test, merge
    with: { squash: true, engine_can_merge: { expr: "params.engine_can_merge" } }
    routes: { merged: { end: completed } }
    tracker: { on: { merged: done } }
```

What the defaults supply: `success` outcomes go to the next step; `failure`
and `blocked` outcomes escalate; `attempts_exhausted` escalates; the last
step's `merged` exit ends the run. What the fragments supply is in `docs/08`.

---

## 13. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entries 8 to 15.
