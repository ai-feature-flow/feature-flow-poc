# Workflow language design

Status: draft for brief 2. Companion to `docs/01-architecture-first-pass.md` and `docs/briefs/brief-02-workflow-language.md`.

## 1. Design principles

This language is the workflow contract for the engine. It is intentionally narrow and deterministic.

1. One schema for both templates and plans.
   - A template is a plan with parameters.
   - A run starts from the template with parameters bound.
   - A re-plan is a new plan revision, not a new syntactic form.

2. Routing is by outcome, not by ad hoc scripting.
   - Steps return one outcome from a fixed vocabulary.
   - The plan defines which outcome routes to which next step or branch.

3. Expressions are deterministic and side-effect free.
   - The language supports parameter references, comparisons, boolean logic, arithmetic, and simple collections.
   - It does not allow general scripting, shelling, file access or network calls.

4. Patches are domain operations.
   - Re-plans and steering use explicit patch operations instead of raw JSON Patch.
   - The engine validates patch semantics before it applies them.

5. Engine mechanism vs workflow policy remains strict.
   - The engine owns step semantics, routing, gate enforcement, protection and validation.
   - The workflow library owns prompts, roles, skill selection, tactics, merge policy and review policy.

## 2. Core concepts

### 2.1 Plan

A `Plan` is the executable data the interpreter runs. It contains:

- `metadata`: plan identity, version, scope and authoring context
- `parameters`: declared inputs with default values and validation
- `steps`: ordered step graph
- `routes`: route definitions keyed by step outcome
- `parallels`: explicit parallel branch definitions when used
- `policy`: optional policy references that are resolved by the engine

A plan is versioned separately from the interpreter. A run always works from a specific plan revision.

### 2.2 Template

A template is a plan whose parameters are not yet bound. A run resolves the template by binding parameter values and then validates the resulting plan.

This is the single-schema model required by L1.

### 2.3 Step

A step is a node in the plan graph. Every step has:

- `id`: unique within the plan
- `kind`: one of the engine step kinds
- `scope`: `ticket`, `epic`, `delivery`, or `any`
- `inputs`: literal or parameter-bound values
- `requires`: provider capabilities or library capabilities
- `outcomes`: named routing destinations for result outcomes
- `protected`: whether the step may not be removed or weakened by a patch
- `attempts`: optional override of default budget
- `timeout`: optional step-specific limits

### 2.4 Outcome routing

Each step resolves to exactly one outcome:

- `completed`
- `failed`
- `blocked`
- `attempts_exhausted`
- `partial`
- `approved`
- `rejected`
- `ready`
- `findings_open`
- `replan_requested`
- `replan_parent`

Routing is explicit:

```yaml
routes:
  - from: agent.run
    on: completed
    to: test.run_tiers
  - from: agent.run
    on: failed
    to: human.gate
  - from: test.run_tiers
    on: findings_open
    to: fix.verify
```

A step without an explicit route for an outcome is treated as a validation error unless the omitted outcome is deliberately blocked by policy.

## 3. Expression language

The language supports a small, deterministic expression model inspired by CEL, but constrained to workflow semantics.

### 3.1 Scope

Expressions are valid in these places:

- conditional route guards
- parameter defaults and validation
- step input value binding
- `when` clauses in loops and branch selection
- policy references and approvals

### 3.2 Allowed operations

- literals: strings, numbers, booleans, null
- arithmetic: `+`, `-`, `*`, `/`, `%`
- comparison: `==`, `!=`, `>`, `>=`, `<`, `<=`
- boolean: `&&`, `||`, `!`
- collection predicates: `contains`, `all`, `any`, `length`
- string helpers: `matches`, `startsWith`, `endsWith`, `lower`, `upper`
- object access: `.field`, `['field']`
- parameter access: `params.ticket.id`, `params.repo`, `facts.reviewOutcome`

### 3.3 Rejected operations

The language must not allow:

- function calls to arbitrary code
- file I/O
- network I/O
- access to wall clock or random values
- mutation of any in-memory object
- unbounded loops or recursion
- shell execution

The engine must reject any expression containing unsupported functions or variable references that do not resolve.

### 3.4 Examples

```yaml
when: "params.forceReplan == true || facts.findingCount == 0"

when: "length(facts.pendingFiles) > 0 && facts.reviewApproved == true"

when: "contains(params.allowedRepoNames, repo.name)"
```

The expression evaluator is side-effect free and deterministic. Expression evaluation errors are validation errors, not runtime surprises.

## 4. Plan schema and template binding

### 4.1 Single schema

The plan schema is the same for templates and plans. The difference is only whether parameters are bound.

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: ticket-delivery
  scope: ticket
  version: 1
parameters:
  ticketId:
    type: string
    required: true
  repo:
    type: string
    default: "service"
  allowMerge:
    type: boolean
    default: false
steps:
  - id: plan
    kind: agent.run
    role: planner
    inputs:
      taskRef: lib/tasks/plan-ticket
      params:
        ticketId: "${params.ticketId}"
    outcomes:
      completed: design
      failed: human.gate
  - id: design
    kind: agent.run
    role: designer
    protected: true
    inputs:
      taskRef: lib/tasks/design
    outcomes:
      completed: implement
      blocked: human.gate
```

### 4.2 Parameter binding

A template parameter is declared with:

```yaml
parameters:
  ticketId:
    type: string
    required: true
```

A run binds it as:

```yaml
parameters:
  ticketId: "FF-1042"
```

The engine resolves `${...}` references before the plan executes. If any required parameter remains unresolved, validation fails before the run starts.

## 5. Step model

### 5.1 Step kinds

The workflow interpreter supports a fixed step catalog; the language defines how step data is expressed, not arbitrary user-defined steps.

The catalog is defined by the engine and includes:

- `agent.run`
- `human.gate`
- `human.session`
- `scm.checkout`
- `scm.commit`
- `scm.merge`
- `test.run_tiers`
- `fix.verify`
- `delivery.deploy`
- `notify.send`
- `tracker.comment`
- `parallel`
- `plan.replan`

The engine may add new step kinds only via versioned engine updates; workflow content does not define new step semantics.

### 5.2 Step inputs and outputs

Step inputs are plain data objects. They must not contain secrets, large transcripts, or raw provider payloads. They store artifact references, IDs and configuration values.

Outputs are not emitted inline into the plan. They are recorded in the record store and referenced by artifact id or run state pointer.

## 6. Parallel steps and joins

The plan supports parallel fan-out via an explicit `parallel` step, with a join policy.

```yaml
- id: validate-epic
  kind: parallel
  inputs:
    branches:
      - id: ticket-a
        planRef: lib/templates/ticket
      - id: ticket-b
        planRef: lib/templates/ticket
      - id: ticket-c
        planRef: lib/templates/ticket
    join: all
    maxConcurrency: 2
  outcomes:
    completed: epic.review
```

Semantics:

- `join: all` waits for every branch to finish before continuing.
- `join: any` continues after the first successful branch, if the workflow policy allows it.
- `maxConcurrency` caps the number of active branches.
- Child plans are generated from the same template schema and bound with their own parameters.

## 7. Domain patches

The patch model is intentionally not generic JSON Patch. It is a set of workflow-aware patch operations that the engine validates and applies as one transaction.

### 7.1 Patch contract

```yaml
patch:
  planVersion: 2
  changes:
    - op: insert_after
      target: implement
      step:
        id: add-review
        kind: agent.run
        role: reviewer
        inputs:
          taskRef: lib/tasks/review
        outcomes:
          completed: merge
    - op: replace_inputs
      target: test.run_tiers
      values:
        tiers: [unit, integration]
    - op: add_parallel_branch
      target: ticket.sync
      branch: { id: repo-b, planRef: lib/templates/repo-b-ticket }
    - op: change_route
      target: fix.verify
      from: failed
      to: human.gate
```

### 7.2 Supported operations

- `insert_before`
- `insert_after`
- `replace_step`
- `remove_step`
- `replace_inputs`
- `add_parallel_branch`
- `remove_parallel_branch`
- `change_route`
- `set_parameter`
- `override_library_reference`
- `insert_policy_guard`

### 7.3 Guardrails

Each patch is validated against the current plan revision before apply.

Rules:

- protected steps cannot be removed or weakened
- route changes cannot point to an unknown step
- a patch cannot modify completed steps in the same run
- a patch cannot change the plan scope without a valid policy check
- a patch must be auditable and associated with a user, agent, or operator

A patch that would violate these rules is rejected with a validation error.

## 8. Library semantics

The library is not a free-form directory. It is a versioned package model that contains reusable workflow content.

### 8.1 Content types

- `roles`
- `agentTasks`
- `skills`
- `checks`
- `templates`
- `policies`
- `providerMappings`

Each item is versioned and content-addressed when stored in the artifact store.

### 8.2 References

A plan references library content by a stable ref rather than inline content:

```yaml
role: roles/implementer
agentTask: lib/tasks/plan-ticket@1.2.0
skill: lib/skills/patch-review@2.0.1
```

Resolution semantics:

- library references resolve against the active library set for the run
- the run records the exact package and content hash it used
- override resolution is tracked as a run-scoped amendment, not a mutation of the base library

## 9. Mid-run override and audit

Mid-run library override is a first-class, audited action. It is not an ad hoc edit of the plan in place.

A library override may:

- replace an agent task prompt
- alter a skill definition
- fix a check definition
- swap a role mapping

Semantics:

- override applies only to later steps in the run, not already completed ones
- the overridden content is recorded with a timestamp, actor, reason and old/new hash
- the decision is logged to the run timeline and to the findings ledger where relevant

This protects the traceability of an active run while still allowing operational fixes.

## 10. Validation rules

The language has a validation model with multiple phases.

### 10.1 Template load time

The engine validates:

- schema correctness
- route references
- parameter completeness
- duplicate step ids
- unknown step kinds
- unresolved `${...}` references
- invalid expression syntax

These are blocking errors.

### 10.2 Run start validation

Before the run begins, the engine validates:

- provider capability compatibility
- required library content existence
- policy eligibility
- repository manifest compatibility
- access and role constraints

These are blocking for the run.

### 10.3 Patch validation

Before applying any patch, the engine validates:

- patch structure
- step existence and routes
- protection invariants
- scope compatibility
- patch uniqueness and ordering

These are blocking for the patch action.

## 11. Worked example: single-ticket flow

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: implement-ticket
  scope: ticket
parameters:
  ticketId:
    type: string
    required: true
  repo:
    type: string
    default: service
steps:
  - id: plan
    kind: agent.run
    role: planner
    inputs:
      taskRef: lib/tasks/plan-ticket
      repo: "${params.repo}"
      ticketId: "${params.ticketId}"
    outcomes:
      completed: design
      failed: human.gate

  - id: design
    kind: agent.run
    role: designer
    protected: true
    inputs:
      taskRef: lib/tasks/design
    outcomes:
      completed: implement
      blocked: human.gate

  - id: implement
    kind: agent.run
    role: implementer
    inputs:
      taskRef: lib/tasks/implement
    outcomes:
      completed: test.run_tiers
      failed: fix.verify

  - id: test.run_tiers
    kind: test.run_tiers
    inputs:
      tiers: [unit, integration]
    outcomes:
      ready: merge
      findings_open: fix.verify
      blocked: human.gate

  - id: fix.verify
    kind: fix.verify
    inputs:
      allowRegression: true
    outcomes:
      completed: test.run_tiers
      failed: human.gate

  - id: merge
    kind: scm.merge
    inputs:
      strategy: squash
    outcomes:
      completed: completed
      rejected: human.gate
```

## 12. Decision summary for L1-L12

The language design confirms the agreed default positions:

- L1: one schema; template is a plan with parameters
- L2: route on outcomes plus a small deterministic expression language; no general scripting
- L3: domain patch operations, not generic JSON Patch
- L4: roles are library content; the engine enforces `write_access`
- L5: tasks and skills resolve by library ref and content hash
- L6: artifact contracts are first-class and validated at both producer and consumer boundaries
- L7: library packages are versioned distribution units with override semantics
- L8: mid-run library override is allowed and auditable
- L9: child plans are generated from templates and bound per ticket, with parent-child re-plan semantics
- L10: validation occurs at load, run-start and patch-time with explicit blocking rules
- L11: workflow tests use dry runs, fake providers and recorded histories replayed against templates
- L12: policy references are resolved from the policy model rather than embedded in workflow logic

## 13. Open questions

- Should the expression language support collection comprehensions, or remain strictly a predicate and selection language?
- Should route guards be allowed inline on each route, or only as a top-level `when` on the step or route?
- Is the library allowed to ship templates with internal `defaultRoute` references, or must all routes be explicit?
- Should there be a first-class `plan.replan` step or only a patch event produced by the engine?

These are small design adjustments rather than architecture blockers, and they can be resolved in the first schema draft without affecting the main language model.
