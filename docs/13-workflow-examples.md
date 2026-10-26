# Worked examples

Status: second draft. Date: 2026-10-27. Deliverable 6 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** Every example is checked against `docs/06` and the recorded
decisions. C1-C9 and E7 are fixed: no tier lists, no merge method, no repository
names, no `allowRegression`, review is `review.run` feeding the findings ledger,
`partial` does not route to success, gates name a decision type, roles do not name
a runtime, session checks derive from later steps, and the re-plan example is the
`docs/01` 6.2 scenario.

Four examples: a single ticket, an epic, a mid-run re-plan, and a human session.

---

## 1. Single ticket: implement, test, review, fix loop, merge

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: ticket-delivery
  scope: ticket
  revision: 1
  schemaVersion: 1
  source: feature-flow-default:templates/ticket-delivery

parameters:
  ticket_id: { type: string, required: true }
  repositories: { type: array, required: true }

artifactTypes:
  - name: ticket-plan
    version: 1
    schema_ref: feature-flow-default:schemas/ticket-plan

steps:
  - id: plan_spec
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/planner
      task_ref: feature-flow-default:tasks/plan-ticket
      write_access: none
      bindings:
        ticket_id: { expr: "params.ticket_id" }
    requires: { capabilities: [sandbox, agent_runtime] }
    limits: { attempts: 3, start_to_close: 4h, heartbeat: 30s }
    produces: [{ name: plan, type: ticket-plan }]
    routes:
      completed: design
      blocked: gate_spec
      failed: gate_spec
      attempts_exhausted: gate_spec
      replan_requested: replan

  - id: design
    kind: agent.run
    protected: true
    inputs:
      role: feature-flow-default:roles/designer
      task_ref: feature-flow-default:tasks/design
      write_access: none
    consumes: [{ name: plan, type: ticket-plan, from: plan_spec }]
    routes:
      completed: implement
      blocked: gate_design
      failed: gate_design
      attempts_exhausted: gate_design
      replan_requested: replan

  - id: implement
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/implementer
      task_ref: feature-flow-default:tasks/implement-ticket
      write_access: branch
      resume_strategy: wip_commits_and_session
    requires: { capabilities: [sandbox, agent_runtime] }
    limits: { attempts: 3, start_to_close: 4h, heartbeat: 30s }
    routes:
      completed: run_tests
      blocked: replan
      failed: replan
      attempts_exhausted: gate_escalate
      replan_requested: replan

  - id: run_tests
    kind: test.run_tiers
    inputs:
      tier_tags: { expr: "steps.plan_spec.artifacts.plan.verification.tier_tags" }
    routes:
      completed: review
      failed: triage_findings
      attempts_exhausted: gate_escalate

  - id: review
    kind: review.run
    inputs:
      scope: ticket
      diff_ref: { expr: "steps.implement.artifacts.diff" }
      checks: [feature-flow-default:checks/merge-bar]
      prior_findings: { expr: "run.findings" }
    routes:
      completed: evaluate_readiness
      findings_open: triage_findings
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: triage_findings
    kind: findings.triage
    inputs:
      finding_refs: { expr: "run.findings.open" }
      mode: agent
    limits: { attempts: 2 }
    routes:
      completed: fix_findings
      blocked: gate_escalate
      attempts_exhausted: gate_escalate

  - id: fix_findings
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/fixer
      task_ref: feature-flow-default:tasks/fix-findings
      write_access: branch
    routes:
      completed: verify_fix
      blocked: gate_escalate
      failed: gate_escalate
      attempts_exhausted: gate_escalate
      replan_requested: replan

  - id: verify_fix
    kind: fix.verify
    inputs:
      finding_ref: { expr: "steps.triage_findings.artifacts.selected" }
      diff_ref: { expr: "steps.fix_findings.artifacts.diff" }
    routes:
      completed: run_tests
      failed: triage_findings
      attempts_exhausted: gate_escalate

  - id: evaluate_readiness
    kind: findings.evaluate_readiness
    routes:
      ready: open_pr
      findings_open: triage_findings

  - id: open_pr
    kind: scm.open_pr
    inputs:
      repository: { expr: "params.repositories[0]" }
      branch: { expr: "run.branch" }
      base: { expr: "run.integration_branch" }
      body_ref: feature-flow-default:prompts/pr-body
    routes:
      completed: wait_checks
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: wait_checks
    kind: ci.wait_checks
    inputs:
      repository: { expr: "params.repositories[0]" }
      commit_sha: { expr: "run.head_sha" }
    routes:
      completed: gate_merge
      failed: triage_findings
      blocked: gate_escalate
      attempts_exhausted: gate_escalate

  - id: gate_merge
    kind: human.gate
    gate: { decision_type: ticket.merge_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/merge-approval
      context_refs: { expr: "[steps.review.artifacts.findings]" }
    routes:
      approved: merge_pr
      rejected: "end: rejected"

  - id: merge_pr
    kind: scm.merge
    inputs:
      pr_ref: { expr: "steps.open_pr.artifacts.pr" }
    routes:
      completed: "end: completed"
      blocked: gate_escalate
      rejected: gate_merge
      attempts_exhausted: gate_escalate

  - id: replan
    kind: plan.replan
    inputs:
      reason: { expr: "steps.implement.outcome" }
      context_refs: { expr: "[steps.plan_spec.artifacts.plan]" }
    limits: { attempts: 2 }
    routes:
      replan_requested: implement
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: gate_spec
    kind: human.gate
    gate: { decision_type: ticket.spec_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/spec-approval
      context_refs: { expr: "[steps.plan_spec.artifacts.plan]" }
    routes:
      approved: design
      rejected: "end: rejected"

  - id: gate_design
    kind: human.gate
    gate: { decision_type: ticket.design_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/design-approval
    routes:
      approved: implement
      rejected: "end: rejected"

  - id: gate_escalate
    kind: human.gate
    gate: { decision_type: ticket.escalation }
    inputs:
      prompt_ref: feature-flow-default:prompts/escalation
    routes:
      approved: replan
      rejected: "end: failed"
```

### 1.1 What this example gets right

| Decision | How |
|---|---|
| C1: tiers come from `feature-flow.yaml` | `run_tests` passes `tier_tags`, not a tier list. The step reads the manifest per step |
| C2: no merge method in a template | `merge_pr` passes only `pr_ref`. The organisation allows methods; the repository chooses |
| C3: no installation-specific names | `repositories` is a run parameter. No default repository name |
| C4: no `allowRegression` | `verify_fix` has no such input. The regression test requirement is a step invariant |
| C5: review feeds the findings ledger | `review.run`, then `findings.triage`, then `findings.evaluate_readiness` for `ready` |
| C7: no policy in a template | Every gate names a `decision_type` only |
| B3: the loop is bounded by the engine | `run_tests` to `triage_findings` to `fix_findings` to `verify_fix` back to `run_tests`. The cycle exits through `gate_escalate` on `attempts_exhausted`, and the findings ledger escalates a finding that reappears after two fixes |
| B2: directives, not step ids | `end: completed`, `end: rejected`, `end: failed` |
| B1: one routing form | Per-step `routes` only |
| A4: snake_case | `task_ref`, `write_access`, `tier_tags`, `pr_ref`, `context_refs` |

---

## 2. Epic: plan tickets, run them in parallel, epic review, delivery in order

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: epic-delivery
  scope: epic
  revision: 1
  schemaVersion: 1
  source: feature-flow-default:templates/epic-delivery

parameters:
  epic_id: { type: string, required: true }
  repositories: { type: array, required: true }

artifactTypes:
  - name: ticket-list
    version: 1
    schema_ref: feature-flow-default:schemas/ticket-list

steps:
  - id: plan_tickets
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/planner
      task_ref: feature-flow-default:tasks/plan-epic
      write_access: none
      bindings:
        epic_id: { expr: "params.epic_id" }
    produces: [{ name: tickets, type: ticket-list }]
    routes:
      completed: expand_tickets
      blocked: gate_epic_plan
      failed: gate_epic_plan
      attempts_exhausted: gate_epic_plan
      replan_requested: replan

  - id: expand_tickets
    kind: plan.replan
    inputs:
      reason: "expand ticket list into run.child branches"
      context_refs: { expr: "[steps.plan_tickets.artifacts.tickets]" }
    limits: { attempts: 2 }
    routes:
      replan_requested: tickets
      failed: gate_epic_plan
      attempts_exhausted: gate_epic_plan

  - id: tickets
    kind: parallel
    inputs:
      branches: []
      join: all
      max_concurrency: 2
    routes:
      completed: epic_review
      any_failed: gate_epic_escalate

  - id: epic_review
    kind: review.run
    inputs:
      scope: epic
      diff_ref: { expr: "run.aggregate_diff" }
      checks: [feature-flow-default:checks/epic-review]
      prior_findings: { expr: "run.findings" }
    routes:
      completed: epic_readiness
      findings_open: epic_triage
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: epic_triage
    kind: findings.triage
    inputs:
      finding_refs: { expr: "run.findings.open" }
      mode: agent
    routes:
      completed: epic_review
      blocked: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: epic_readiness
    kind: findings.evaluate_readiness
    routes:
      ready: delivery_order
      findings_open: epic_triage

  - id: delivery_order
    kind: delivery.deploy_order
    inputs:
      repositories: { expr: "params.repositories" }
      order: { expr: "params.repositories" }
    routes:
      completed: deliver

  - id: deliver
    kind: delivery.deploy
    inputs:
      repository: { expr: "steps.delivery_order.artifacts.next" }
    routes:
      completed: delivery_report
      failed: delivery_gate
      blocked: delivery_gate
      attempts_exhausted: delivery_gate

  - id: delivery_gate
    kind: delivery.gate
    gate: { decision_type: delivery.partial_approval }
    inputs:
      status: partial
    routes:
      approved: deliver
      rejected: "end: partial"

  - id: delivery_report
    kind: delivery.report
    inputs:
      ledger_ref: { expr: "run.delivery_ledger" }
    routes:
      completed: "end: completed"
      attempts_exhausted: "end: completed"

  - id: replan
    kind: plan.replan
    inputs:
      reason: { expr: "steps.plan_tickets.outcome" }
    routes:
      replan_requested: tickets
      failed: gate_epic_plan
      attempts_exhausted: gate_epic_plan

  - id: gate_epic_plan
    kind: human.gate
    gate: { decision_type: epic.plan_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/epic-plan-approval
    routes:
      approved: expand_tickets
      rejected: "end: rejected"

  - id: gate_epic_escalate
    kind: human.gate
    gate: { decision_type: epic.escalation }
    inputs:
      prompt_ref: feature-flow-default:prompts/escalation
    routes:
      approved: replan
      rejected: "end: failed"
```

### 2.1 The ticket expansion mechanism (D2)

`tickets` starts with `branches: []`. `expand_tickets` is a `plan.replan` step
whose agent consumes the `ticket-list` artifact and returns a patch adding one
`run.child` per ticket. The patch, for a three-ticket epic:

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-5678
  proposed_by: { type: agent, step_id: expand_tickets }
  reason: "epic plan produced three tickets"
base_revision: 1
changes:
  - op: add_branch
    target: tickets
    step:
      id: ticket_1
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        repositories: { expr: "params.repositories" }
      routes:
        completed: "end: completed"
        failed: gate_epic_escalate
        replan_parent: apply_child_patch
        attempts_exhausted: gate_epic_escalate
  - op: add_branch
    target: tickets
    step: { id: ticket_2, kind: run.child, inputs: { ... }, routes: { ... } }
  - op: add_branch
    target: tickets
    step: { id: ticket_3, kind: run.child, inputs: { ... }, routes: { ... } }
```

The same template is reused across branches (review 09, answer 13). That is the
normal ticket case.

### 2.2 What this example gets right

| Decision | How |
|---|---|
| D2: no hard-coded ticket ids | `branches: []`, filled by a patch from the `ticket-list` artifact |
| A5: a ticket branch is a `run.child` | Each branch is `run.child` with `plan_ref` |
| A3: join semantics | `join: all`, `max_concurrency: 2`. `any_failed` routes to a gate |
| C5: epic review is `review.run` | On the aggregate diff, then `findings.evaluate_readiness` |
| C6: `partial` does not route to success | `delivery_gate` routes `rejected` to `end: partial`, never `end: completed` |
| C3: no repository names | `repositories` is a run parameter; order comes from plan data |

---

## 3. Mid-run re-plan: a ticket discovers a second repository

Review 09 E7. This is the `docs/01` section 6.2 scenario: a ticket returns
`replan_parent` with a patch adding a `run.child` for the second repository to the
**epic** plan.

The ticket run's `implement` step discovers the change is needed in a second
repository. It routes to `replan`, whose agent produces a patch for the parent.

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-5678
  proposed_by:
    type: child_run
    child_run_id: run-5678-ticket-2
    step_id: replan
  reason: "ticket 2 needs a change in the platform repository"
base_revision: 4
changes:
  - op: add_branch
    target: tickets
    step:
      id: ticket_2_platform
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        repositories: { expr: "params.platform_repository" }
      routes:
        completed: "end: completed"
        failed: gate_epic_escalate
        replan_parent: apply_child_patch
        attempts_exhausted: gate_epic_escalate
  - op: insert_after
    target: epic_review
    step:
      id: cross_repo_review
      kind: review.run
      inputs:
        scope: epic
        diff_ref: { expr: "run.aggregate_diff" }
        checks: [feature-flow-default:checks/cross-repo-contract]
        prior_findings: { expr: "run.findings" }
      routes:
        completed: epic_readiness
        findings_open: epic_triage
        failed: gate_epic_escalate
        attempts_exhausted: gate_epic_escalate
  - op: change_route
    target: epic_review
    outcome: completed
    to: cross_repo_review
```

### 3.1 What happens

1. The child run returns `replan_parent` with this patch.
2. The parent applies it through the same `applyPatch` (`docs/01` 3.4).
3. `base_revision: 4` must be the parent's current revision. If the parent has
   moved on, the patch is rejected as stale and re-proposed (E3).
4. Already-completed steps are untouched. `design` is protected and intact.
5. The new child run starts. `cross_repo_review` now sits between `epic_review`
   and `epic_readiness`.

### 3.2 If the patch had touched protected content

If the patch had included `remove_step` on `design`, or a `change_route` that made
`design` unreachable, then because the proposer is an **agent** it would not be
rejected: it would become a **pending human decision** (E2), recorded with the
diff and the reason. A human may approve it with a reason. A patch that would
relax a safety floor is rejected outright with no human override path.

---

## 4. Human session with pre-submit checks

```yaml
  - id: spec_session
    kind: human.session
    inputs:
      purpose: "draft the specification and verification requirements"
      context_refs:
        - { expr: "steps.plan_tickets.artifacts.tickets" }
        - { expr: "run.tracker_issue" }
      checks_from: [plan_review, verification_review]
    produces: [{ name: spec, type: ticket-spec }]
    routes:
      completed: plan_review
      rejected: gate_spec

  - id: plan_review
    kind: review.run
    inputs:
      scope: ticket
      diff_ref: { expr: "steps.spec_session.artifacts.spec" }
      checks: [feature-flow-default:checks/plan-review]
      prior_findings: { expr: "run.findings" }
    routes:
      completed: implement
      findings_open: spec_session
      failed: gate_spec
      attempts_exhausted: gate_spec

  - id: verification_review
    kind: review.run
    inputs:
      scope: ticket
      diff_ref: { expr: "steps.spec_session.artifacts.spec" }
      checks: [feature-flow-default:checks/verification-requirements]
      prior_findings: { expr: "run.findings" }
    routes:
      completed: implement
      findings_open: spec_session
      failed: gate_spec
      attempts_exhausted: gate_spec
```

### 4.1 What this example gets right

| Decision | How |
|---|---|
| C9: checks derive from later steps | `checks_from: [plan_review, verification_review]`. The session offers the validators of the steps that follow it, on demand |
| C9: ids are engine-generated | No `session_id` in the plan |
| D23: no check appears for the first time after submission | The human can run `plan_review` and `verification_review` inside the session, before submitting |
| The trust boundary is the submission | Hooks and policy do not apply in the human's environment (`docs/01` 7.4) |

The prototype's spec session took eight rounds mostly because agent-drafted
verification requirements were accepted without review and then failed the later
plan review. `checks_from` is the mechanism that removes those round trips.

---

## 5. Open questions

- **`delivery.deploy` and `delivery_order`.** Section 2 uses `delivery.deploy`
  once, with `repository` from `delivery_order.artifacts.next`. Is one step
  re-entered per repository, or should the plan contain one `delivery.deploy` per
  repository? `docs/06` 12.2 says one child workflow per repository (DP3), which
  suggests the former, but the plan shape is not explicit.
- **`run.wait` is unused in all four examples.** Section 2 uses `parallel` with
  `run.child` branches. Is `run.wait` only for children started outside a
  `parallel`, or is it redundant?
- **`end: partial` and the three delivery choices.** `docs/01` 7.3 gives retry,
  fix forward and revert. Section 2 routes `approved` back to `deliver` (retry)
  and `rejected` to `end: partial`. Fix forward and revert are not shown. Should
  `delivery.gate` carry three options rather than approve and reject?
