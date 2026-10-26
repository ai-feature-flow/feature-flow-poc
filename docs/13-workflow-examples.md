# Worked examples

Status: draft. Companion to `docs/09-workflow-language.md` and `docs/10-workflow-schema.md`.

## 1. Single-ticket workflow

This is the common path: a ticket is planned, implemented, tested, reviewed and merged.

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
      ticketId: "${params.ticketId}"
      repo: "${params.repo}"
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

This path is intentionally simple. The loop is: implement → test → fix → retest → merge.

## 2. Epic workflow with parallel tickets

This is the parent-child pattern for a multi-ticket delivery.

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: deliver-epic
  scope: epic
parameters:
  epicId:
    type: string
    required: true
steps:
  - id: plan-tickets
    kind: agent.run
    role: planner
    inputs:
      taskRef: lib/tasks/plan-epic
      epicId: "${params.epicId}"
    outcomes:
      completed: ticket.fanout
      failed: human.gate

  - id: ticket.fanout
    kind: parallel
    inputs:
      branches:
        - id: ticket-1
          planRef: lib/templates/ticket-delivery
          params:
            ticketId: "EPIC-101"
        - id: ticket-2
          planRef: lib/templates/ticket-delivery
          params:
            ticketId: "EPIC-102"
        - id: ticket-3
          planRef: lib/templates/ticket-delivery
          params:
            ticketId: "EPIC-103"
      join: all
      maxConcurrency: 2
    outcomes:
      completed: epic.review

  - id: epic.review
    kind: agent.run
    role: reviewer
    inputs:
      taskRef: lib/tasks/epic-review
    outcomes:
      ready: delivery.merge
      findings_open: human.gate

  - id: delivery.merge
    kind: delivery.deploy
    inputs:
      repos: [service, platform]
    outcomes:
      completed: completed
      partial: completed
      blocked: human.gate
```

This example demonstrates:

- parent plan controlling child work
- parallel ticket execution within one repository set
- a global epic-level review gate before delivery

## 3. Mid-run re-plan: add a second repository ticket

A running ticket plan can be amended by a patch that adds a parallel branch.

```yaml
apiVersion: feature-flow/v1
kind: Patch
metadata:
  runId: run-1234
  reason: "add second repo workstream"
planVersion: 2
changes:
  - op: add_parallel_branch
    target: delivery.sync
    branch:
      id: repo-b-ticket
      planRef: lib/templates/repo-ticket
      params:
        ticketId: "FF-1176"
        repo: platform
  - op: change_route
    target: delivery.sync
    from: completed
    to: epic.review
```

Rules:

- the patch as written is allowed only if the target is not protected and the run is still in a future-only portion
- if it touches a protected step or a completed step, the patch is rejected
- the engine records the patch with the actor, timestamp and reason

## 4. Human session with pre-submit checks

```yaml
- id: human.session
  kind: human.session
  inputs:
    sessionId: "session-9b2"
    purpose: "manual merge validation"
    contextRefs:
      - artifact:ref/plan-128
      - artifact:ref/changeset-88
    checks:
      - merge-bar
      - security-scan
      - policy-approval
  outcomes:
    completed: completed
    rejected: human.gate
```

This step shows the design principle: the surface prepares the workspace, but the engine performs validation before submission and records the final decision in the audit trail.

## 5. Human gate with approval policy

```yaml
- id: release.gate
  kind: human.gate
  inputs:
    gateId: "release-gate-421"
    prompt: "Approve the release branch merge"
    requiredRole: approver
    minApprovers: 1
    options: [approve, reject]
  outcomes:
    approved: completed
    rejected: rejected
```

The gate is resolved through the API and the policy model, not by arbitrary step logic.

## 6. Open questions

- Should the expression language allow inline route guards without a separate `routes` list?
- Should `parallel` branch definitions be allowed to reuse the same template multiple times with different parameter sets?
- Are there any workflow patterns that require `any`-join semantics beyond the current language model?
