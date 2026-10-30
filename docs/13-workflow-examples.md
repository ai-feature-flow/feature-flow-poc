# Worked examples

Status: fourth draft. Date: 2026-10-31. Deliverable 6 of
`docs/briefs/brief-02-workflow-language.md` (the workflow language brief).

Example plans and patches written in the workflow language. `docs/09` owns what
the language means, `docs/10` owns the JSON Schema it must conform to, and
`docs/06` (the step catalogue) owns each step kind's inputs and outcomes.

| Section | Example |
|---|---|
| 1 | A single ticket: plan, implement, test, review, fix loop, catch up, merge |
| 2 | An epic: plan tickets, run them in parallel, epic review, ordered delivery |
| 3 | A mid-run re-plan: a ticket discovers a second repository |
| 4 | A human session with pre-submit checks |
| 5 | The epic fix path as a separate fix ticket |
| 6 | How these examples were checked |
| 7 | Open questions |

This draft brings in three later decisions: the merge design (`docs/20`:
tickets merge without a lock), the review design (`docs/19`: the reviewer is an
agent on a read-only checkout), and design requirement DR12 (routing to a step
that already ran runs it again).

---

## 1. Single ticket: implement, test, review, fix loop, merge

A ticket run works on its own ticket branch and merges into its epic's
**integration branch** (the branch every ticket of the epic merges into before
the epic is delivered to `main`).

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: ticket-delivery
  scope: ticket
  revision: 1
  source: feature-flow-default:templates/ticket-delivery

parameters:
  ticket_id: { type: string, required: true }
  repositories: { type: array, required: true }
  epic_contract:
    type: string
    required: false
    description: "Artifact reference to the parent epic's contract, given to the ticket review"

artifact_types:
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
    limits: { attempts: 3, start_to_close: 4h, heartbeat: 30s, correction_turns: 2 }
    produces: [{ name: plan, type: ticket-plan }]
    outputs:
      tier_tags: { type: array, max_size: 16 }
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
      bindings:
        plan_path: { from_artifact: "steps.plan_spec.artifacts.plan" }
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
      bindings:
        plan_path: { from_artifact: "steps.plan_spec.artifacts.plan" }
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
      tier_tags: { expr: "steps.plan_spec.outputs.tier_tags" }
    consumes: [{ name: plan, type: ticket-plan, from: plan_spec }]
    routes:
      completed: review
      failed: triage_findings
      attempts_exhausted: gate_escalate

  - id: review
    kind: review.run
    inputs:
      scope: ticket
      branch: { expr: "run.branch" }
      base: { expr: "run.integration_branch" }
      context_refs: { expr: "[steps.plan_spec.artifacts.plan, steps.run_tests.artifacts.results, params.epic_contract]" }
      checks: [feature-flow-default:checks/merge-bar]
      prior_findings: { expr: "findings.all" }
    routes:
      completed: evaluate_readiness
      findings_open: triage_findings
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: triage_findings
    kind: findings.triage
    inputs:
      finding_refs: { expr: "findings.open" }
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
      bindings:
        findings_path: { from_artifact: "steps.triage_findings.artifacts.selected" }
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
      repository: { expr: "run.repositories[0]" }
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
      repository: { expr: "run.repositories[0]" }
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
      approved: catch_up
      rejected: { end: rejected }

  - id: catch_up
    kind: scm.merge_base_forward
    inputs:
      repository: { expr: "run.repositories[0]" }
      branch: { expr: "run.integration_branch" }   # merged from
      into: { expr: "run.branch" }                 # merged into
    outputs:
      branch_changed: { type: boolean }
    routes:
      completed:
        - when: "steps.catch_up.outputs.branch_changed == true"
          to: retest
        - to: merge_pr
      blocked: resolve_conflict
      attempts_exhausted: gate_escalate

  - id: retest
    kind: test.run_tiers
    inputs:
      tier_tags: { expr: "steps.plan_spec.outputs.tier_tags" }
    consumes: [{ name: plan, type: ticket-plan, from: plan_spec }]
    routes:
      completed: merge_pr
      failed: triage_findings
      attempts_exhausted: gate_escalate

  - id: merge_pr
    kind: scm.merge
    inputs:
      pr_ref: { expr: "steps.open_pr.artifacts.pr" }
    limits: { attempts: 3 }
    routes:
      completed: { end: completed }
      blocked: catch_up
      rejected: gate_merge
      attempts_exhausted: gate_escalate

  - id: resolve_conflict
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/implementer
      task_ref: feature-flow-default:tasks/resolve-merge-conflict
      write_access: branch
    routes:
      completed: run_tests
      blocked: gate_escalate
      failed: gate_escalate
      attempts_exhausted: gate_escalate
      replan_requested: replan

  - id: replan
    kind: plan.replan
    inputs:
      mode: agent
      reason: { expr: "steps.implement.outcome" }
      context_refs: { expr: "[steps.plan_spec.artifacts.plan]" }
    limits: { attempts: 2 }
    routes:
      replan_requested: { resume: true }
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
      rejected: { end: rejected }

  - id: gate_design
    kind: human.gate
    gate: { decision_type: ticket.design_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/design-approval
    routes:
      approved: implement
      rejected: { end: rejected }

  - id: gate_escalate
    kind: human.gate
    gate: { decision_type: ticket.escalation }
    inputs:
      prompt_ref: feature-flow-default:prompts/escalation
    routes:
      approved: replan
      rejected: { end: failed }
```

### 1.1 What this example shows

| Rule | How the example follows it |
|---|---|
| Test tiers are repository facts, not plan content (review 09 C1) | `run_tests` selects tiers by tag. `tier_tags` comes from `plan_spec`'s published outputs; the tiers themselves are defined in the repository's `feature-flow.yaml` |
| Expressions never read artifact content (review 10 A3) | `tier_tags` is a small published output. `run_tests` reads the plan artifact itself, inside its activity |
| Every `run` field used is defined | `run.repositories[0]`, `run.branch`, `run.integration_branch` and `run.head_sha` are all in `docs/09` section 8.3.1 (the `run` namespace) |
| `findings` is a namespace, not a `run` field | `findings.all`, `findings.open` |
| No merge method in a template (review 09 C2) | `merge_pr` passes only `pr_ref`; the repository's manifest names the method |
| No installation-specific names (review 09 C3) | `repositories` is a run parameter |
| No switch to skip regression tests (review 09 C4) | `verify_fix` has no such input; the regression-test rule is a step invariant (`docs/06` section 10.1, `fix.verify`) |
| Review feeds the findings ledger (review 09 C5) | `review.run`, `findings.triage` and `findings.evaluate_readiness` read and write the ledger, the record of every finding and its status |
| No policy in a template (review 09 C7) | Every gate names only a `decision_type`. The organisation's policy decides who may approve |
| Privilege comes from the role (review 10 A1) | A step's `write_access` may only tighten its role's value. No step sets `tools` or `capabilities` |
| Agents never hold a credential that can push (DR13) | `implement`, `fix_findings` and `resolve_conflict` have `write_access: branch`: the agent commits in its own sandbox, and the engine pushes the branch at the end of the step and at work-in-progress points |
| Correction turns for a malformed artifact (review 10 B5) | `plan_spec` sets `correction_turns: 2`. The schema does not accept this field yet (section 7) |
| Bindings pass file paths (review 10 B6) | `plan_path: { from_artifact: ... }` gives the agent a path, not a content hash |
| Directives are mappings (review 10 B9) | `{ end: completed }` needs no YAML quoting |
| A re-plan names where to resume (review 10 A6) | `replan` routes `{ resume: true }`; the patch it produces names `resume_at` |
| Loops re-run steps (DR12) | `verify_fix` routes to `run_tests`, and `merge_pr` routes back to `catch_up`. Both targets have already run; the interpreter runs them again and clears their previous outcome |
| Loops are bounded by the engine (review 09 B3, DR10) | Every entry to a step counts against its `limits.attempts`, including re-entry from a loop. A spent allowance returns `attempts_exhausted`, routed to `gate_escalate` |

### 1.2 How the ticket merges

Merge design, `docs/20`. There is no merge lock: the ticket merges the way a
developer does.

| Step | What happens |
|---|---|
| `gate_merge` | Approval to merge into the integration branch. Policy may approve it automatically. A merge into the default branch always needs a human (floor F1), but this merge targets the integration branch |
| `catch_up` (`scm.merge_base_forward`) | The engine merges the latest integration branch into the ticket branch. `branch` is the branch merged **from** (integration), `into` the branch merged **into** (ticket). It publishes `branch_changed` |
| `retest` | Runs only if catch-up changed the branch. Re-runs the ticket's test tiers on the new head |
| `merge_pr` (`scm.merge`) | The engine builds the merge commit from exactly what was tested and pushes it to the integration branch. Git refuses the push if the integration branch moved since catch-up (another ticket merged first). The code host marks the pull request merged |
| Refused merge | `merge_pr` returns `blocked` and routes back to `catch_up` |
| Retry cap | `merge_pr` has `limits: { attempts: 3 }`. Each refusal re-enters it, so after three entries the next returns `attempts_exhausted` and the ticket escalates to a human. A slow ticket cannot be starved forever by faster siblings |
| Conflict | `catch_up` returns `blocked`, and an agent resolves it (`resolve_conflict`), not a human first (`docs/06` section 6.2). The ticket then goes back through `run_tests`, `review` and `gate_merge`, so resolution code is never merged unreviewed |

Nothing untested can land: the push succeeds only if the integration branch is
exactly what the ticket was last tested against. Agents never merge or push; the
engine does both (DR13).

### 1.3 How the ticket is reviewed

Review design, `docs/19`.

| Element | In this example |
|---|---|
| Machine checks first | `run_tests` runs before `review`, and its results are passed to the reviewer in `context_refs` |
| The reviewer is an agent on a read-only checkout | `review` names the `branch` and the `base`. The reviewer reads the diff itself and may open any file; nothing else is pre-loaded |
| Review against a specification | `context_refs` carries the ticket plan (its acceptance criteria) and the epic's contract (`params.epic_contract`, passed by the parent epic) |
| Prior findings | `prior_findings` is the findings ledger. A finding fixed and verified stays fixed while the code it points at is unchanged (findings ledger position L2, `docs/17` section 6.5) |
| A different model reviews | Chosen by configuration, not by the template |
| Coverage | The engine records every file the reviewer opens. A review that never opened a changed file is re-run, not accepted. Engine behaviour; nothing in the plan |

---

## 2. Epic: plan tickets, run them in parallel, epic review, delivery in order

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: epic-delivery
  scope: epic
  revision: 1
  source: feature-flow-default:templates/epic-delivery

parameters:
  epic_id: { type: string, required: true }
  repositories: { type: array, required: true }

artifact_types:
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
      mode: expand
      from: { expr: "steps.plan_tickets.artifacts.tickets" }
      mapping:
        template: feature-flow-default:templates/ticket-delivery
        target: tickets
        bind:
          ticket_id: "$.ticket_id"
          repositories: "$.repositories"
      resume_at: tickets
    limits: { attempts: 2 }
    routes:
      replan_requested: { resume: true }
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
      branch: { expr: "run.integration_branch" }
      context_refs: { expr: "[steps.plan_tickets.artifacts.tickets]" }
      checks: [feature-flow-default:checks/epic-review]
      prior_findings: { expr: "findings.all" }
    routes:
      completed: epic_readiness
      findings_open: epic_triage
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: epic_triage
    kind: findings.triage
    inputs:
      finding_refs: { expr: "findings.open" }
      mode: agent
    routes:
      completed: epic_fix
      blocked: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: epic_fix
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/fixer
      task_ref: feature-flow-default:tasks/fix-epic-findings
      write_access: branch
      bindings:
        findings_path: { from_artifact: "steps.epic_triage.artifacts.selected" }
    routes:
      completed: epic_review
      blocked: gate_epic_escalate
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate
      replan_requested: replan

  - id: epic_readiness
    kind: findings.evaluate_readiness
    routes:
      ready: expand_delivery
      findings_open: epic_triage

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
    routes:
      replan_requested: { resume: true }
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: delivery_start
    kind: delivery.deploy_order
    inputs:
      repositories: { expr: "params.repositories" }
      order: { expr: "params.repositories" }
    routes:
      completed: delivery_report

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
        - to: delivery_start
      rejected: { end: partial }

  - id: revert_prs
    kind: scm.revert
    inputs:
      pr_ref: { expr: "run.delivery_prs" }
    routes:
      completed: gate_revert
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: gate_revert
    kind: human.gate
    gate: { decision_type: delivery.revert_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/revert-approval
    routes:
      approved: { end: partial }
      rejected: { end: partial }

  - id: delivery_report
    kind: delivery.report
    inputs:
      ledger_ref: { expr: "run.delivery_ledger" }
    routes:
      completed: { end: completed }
      attempts_exhausted: { end: completed }

  - id: replan
    kind: plan.replan
    inputs:
      mode: agent
      reason: { expr: "steps.plan_tickets.outcome" }
    routes:
      replan_requested: { resume: true }
      failed: gate_epic_plan
      attempts_exhausted: gate_epic_plan

  - id: gate_epic_plan
    kind: human.gate
    gate: { decision_type: epic.plan_approval }
    inputs:
      prompt_ref: feature-flow-default:prompts/epic-plan-approval
    routes:
      approved: expand_tickets
      rejected: { end: rejected }

  - id: gate_epic_escalate
    kind: human.gate
    gate: { decision_type: epic.escalation }
    inputs:
      prompt_ref: feature-flow-default:prompts/escalation
    routes:
      approved: replan
      rejected: { end: failed }
```

### 2.1 Ticket expansion

`tickets` is a `parallel` step that starts with no branches (`branches: []`).
`expand_tickets` is a `plan.replan` in `mode: expand`: a deterministic expansion
that runs **no agent** (review 10 B1). It maps each item of the `ticket-list`
artifact to a `run.child` (a child ticket run) and returns a patch. For a
three-ticket epic:

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-5678
  proposed_by: { type: engine, step_id: expand_tickets }
  reason: "epic plan produced three tickets"
base_revision: 1
resume_at: tickets
changes:
  - op: add_branch
    target: tickets
    step:
      id: ticket_1
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        parameters:
          ticket_id: "EPIC-101"
          repositories: ["acme/api"]
  - op: add_branch
    target: tickets
    step:
      id: ticket_2
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        parameters:
          ticket_id: "EPIC-102"
          repositories: ["acme/api"]
  - op: add_branch
    target: tickets
    step:
      id: ticket_3
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        parameters:
          ticket_id: "EPIC-103"
          repositories: ["acme/api", "acme/web"]
```

Rules this patch follows:

| Rule | Why |
|---|---|
| A branch has **no routes at all** (review 11 A1, review 10 A2) | Its terminal outcome returns to the join. `blocked` is handled inside the child's own plan. A child's request to re-plan the epic (`replan_parent`) is handled by the interpreter, and siblings keep running |
| `ticket_id` is passed to every branch (review 10 A5) | `ticket-delivery` declares it required; a branch without it fails check 6 (unbound required parameter) at binding |
| The patch carries the **bound values** (review 11 B4) | The mapping binds `repositories: "$.repositories"` from each ticket-list item, so the patch holds each ticket's list, not `params.repositories` |
| `proposed_by.type` is `engine` | No agent ran, so the audit record does not attribute the change to one |

### 2.2 Ordered two-repository delivery

`expand_delivery` is the same deterministic expansion, applied to repositories.
It produces one `delivery.deploy` per repository, chained in order:

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-5678
  proposed_by: { type: engine, step_id: expand_delivery }
  reason: "delivery across two repositories in declared order"
base_revision: 3
resume_at: delivery_start
changes:
  - op: add_chain
    target: delivery_start
    on_other: delivery_gate
    step:
      id: deliver_api
      kind: delivery.deploy
      inputs:
        repository: acme/api
  - op: add_chain
    target: deliver_api
    on_other: delivery_gate
    step:
      id: deliver_web
      kind: delivery.deploy
      inputs:
        repository: acme/web
```

`add_chain` points `target`'s `completed` route at the new step, and every
other outcome at `on_other`. So `deliver_api` completing goes to `deliver_web`;
`deliver_api` failing goes to `delivery_gate`, and `deliver_web` never runs.

**Order is a declared dependency, not a concurrency limit** (review 11 A2). A
`parallel` with `max_concurrency: 1` would still run `deliver_web` after
`deliver_api` failed, because `join: all` waits for every branch.

The deployment environment is not a step input. It is an installation-specific
name (the problem review 09 C3 removed) and comes from configuration.

### 2.3 The epic fix path

`epic_triage` routes to `epic_fix`, an `agent.run` that fixes epic findings on
the integration branch and then routes back to `epic_review`. The agent commits
in its sandbox; the engine pushes the integration branch (DR13). The route back
re-runs `epic_review`, which already has an outcome (DR12). This path covers a
defect that exists only once tickets are combined, the cross-ticket case the
epic review exists for (review 08, answer 4).

### 2.4 What this example shows

| Rule | How the example follows it |
|---|---|
| No hard-coded ticket ids, no agent in expansion (review 09 D2, review 10 B1) | `branches: []`, filled by a deterministic `mode: expand` patch |
| A ticket branch is a child run (review 10 A5) | Each branch is a `run.child` with `plan_ref` and a bound `ticket_id` |
| Branch completion returns to the join (review 10 A2) | No routes on branches; the join aggregates |
| Join semantics | `join: all`, `max_concurrency: 2`; `any_failed` goes to a gate |
| The epic review sees the whole epic (review design, `docs/19`) | `epic_review` names the integration branch; the reviewer compares it with the default branch, looking for problems that exist only once every ticket is combined |
| Epic prior findings include the tickets' findings (DR11) | `findings.all` at epic scope includes each child ticket's findings ledger. Engine behaviour; the plan does not list them |
| The epic fix path (review 10 A10) | `epic_triage` to `epic_fix` to `epic_review` |
| Ordered delivery (review 10 A11) | One `delivery.deploy` per repository, chained by `add_chain` |
| A gate may record a choice (review 10 B12) | `delivery_gate` records `retry`, `fix_forward` or `revert`; guarded routes select on it |
| `partial` never routes to success (review 09 C6) | `rejected: { end: partial }`, never `{ end: completed }` |
| A re-plan names where to resume (review 10 A6) | Every `plan.replan` routes `{ resume: true }`, with `resume_at` in its inputs |

---

## 3. Mid-run re-plan: a ticket discovers a second repository

The scenario of `docs/01` section 6.2 (an epic with a mid-run re-plan). A ticket
returns `replan_parent` with a patch that adds a `run.child` for a second
repository to the **epic** plan, and adds a cross-repository review after the
epic review.

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
resume_at: tickets
changes:
  - op: add_branch
    target: tickets
    step:
      id: ticket_2_platform
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        parameters:
          ticket_id: "EPIC-102-PLATFORM"
          repositories: ["acme/platform"]
  - op: insert_after
    target: epic_review
    step:
      id: cross_repo_review
      kind: review.run
      inputs:
        scope: epic
        branch: { expr: "run.integration_branch" }
        context_refs: { expr: "[steps.plan_tickets.artifacts.tickets]" }
        checks: [feature-flow-default:checks/cross-repo-contract]
        prior_findings: { expr: "findings.all" }
      routes:
        completed: epic_readiness
        findings_open: epic_triage
        failed: gate_epic_escalate
        attempts_exhausted: gate_epic_escalate
```

### 3.1 What happens

1. The child run returns `replan_parent` with this patch.
2. The parent applies it through the single patch path, `applyPatch` (`docs/01`
   section 3.4: re-planning and steering share one path).
3. `base_revision: 4` must be the parent's current plan revision. If the parent
   has moved on, the patch is rejected as stale and re-proposed (review 09 E3).
4. `resume_at: tickets`: execution continues at the `tickets` parallel step,
   which starts the new branch.
5. Steps that already completed are untouched. The protected `design` step is
   intact.
6. `insert_after epic_review` points `epic_review`'s `completed` route at
   `cross_repo_review` (review 10 A7). No separate `change_route` is needed.

### 3.2 If the patch had touched protected content

Suppose the patch had removed `design` (a protected step) or made it unreachable.
Because the proposer is an **agent**, the patch is not rejected: it becomes a
**pending human decision**, recorded with the diff and the reason (review 09
E2). A human may approve it with a reason. A patch that would relax a safety
floor (the organisation-wide minimums no workflow may relax, `docs/07` section
2) is rejected outright, with no human override.

---

## 4. Human session with pre-submit checks

A fragment of a ticket plan. A human drafts the specification in an interactive
session, and can run the later automated reviews from inside that session
before submitting.

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
      prior_findings: { expr: "findings.all" }
    routes:
      completed: verification_review
      findings_open: spec_session
      failed: gate_spec
      attempts_exhausted: gate_spec

  - id: verification_review
    kind: review.run
    inputs:
      scope: ticket
      diff_ref: { expr: "steps.spec_session.artifacts.spec" }
      checks: [feature-flow-default:checks/verification-requirements]
      prior_findings: { expr: "findings.all" }
    routes:
      completed: implement
      findings_open: spec_session
      failed: gate_spec
      attempts_exhausted: gate_spec
```

### 4.1 What this example shows

| Rule | How the example follows it |
|---|---|
| Pre-submit checks are the later steps' own checks (review 09 C9) | `checks_from: [plan_review, verification_review]` |
| Ids are engine-generated (review 09 C9) | No session id in the plan |
| Every step is reachable (check 13) | `plan_review` routes `completed` to `verification_review` |
| No check appears for the first time after submission (engine design decision D23) | Both reviews can be run inside the session, before submitting |
| Pre-submit parity (review design, `docs/19`) | A check run from the session must match the gated step: same brief, same tools, same model, same commit |
| The trust boundary is the submission | Hooks and policy do not apply in the human's own environment (`docs/01` section 7.4, interactive human sessions) |

These two reviews check a specification artifact, not a code branch, so they
still pass the artifact as `diff_ref` (section 7).

The prototype's specification session took eight rounds, mostly because
agent-drafted verification requirements were accepted without review and then
failed the later plan review. `checks_from` removes those round trips.

---

## 5. The epic fix path as a fix ticket

Section 2.3 fixes epic findings directly on the integration branch. The
alternative is a **fix ticket added by re-plan**, a `run.child`, which suits a
defect that needs its own branch, tests and review:

```yaml
apiVersion: feature-flow/v1
kind: PlanPatch
metadata:
  run_id: run-5678
  proposed_by: { type: agent, step_id: epic_triage }
  reason: "cross-ticket contract defect needs its own ticket"
base_revision: 5
resume_at: tickets
changes:
  - op: add_branch
    target: tickets
    step:
      id: ticket_fix_contract
      kind: run.child
      inputs:
        child_scope: ticket
        plan_ref: feature-flow-default:templates/ticket-delivery
        parameters:
          ticket_id: "EPIC-FIX-1"
          repositories: ["acme/api", "acme/web"]
```

Both are valid. The integration-branch fix (section 2.3) suits a finding the epic
review raised on the combined change; the fix ticket suits one that needs
independent verification. Which to use is workflow content, not an engine choice
(`docs/01` section 1.5: the engine provides mechanism, workflow content provides
policy). The engine prefers neither.

---

## 6. Validation of these examples

Every example was checked by script on 2026-10-31 against the `docs/10` JSON
Schema and against the validation checks in `docs/12` section 2 (the validation
table), using the outcome list in `docs/09` section 4.2 (which outcomes each
kind may return).

| Check (`docs/12` section 2) | Result |
|---|---|
| 1 Schema conformance | **Fails in two known places**, both schema gaps (section 7): `correction_turns` in example 1, and the route-less steps in every patch (parallel branches and chain steps). Everything else conforms. All blocks parse as YAML; directives are mappings |
| 2 Unknown step kind | Pass. 20 distinct kinds used, all in the `docs/10` `StepKind` list |
| 3 Duplicate step id | Pass |
| 4 Step id looks like a step kind | Pass |
| 5 Undeclared reference | Pass. Every `params.*` used is declared |
| 6 Unbound required parameter | Pass. Every `run.child` branch passes `ticket_id` and `repositories`; `epic_contract` is optional |
| 7-9 Expression parses, type checks, within bounds | Pass by inspection. All expressions use declared namespaces |
| 10 Every outcome the kind can return is routed | Pass, by script, for both full plans, the fragment and every patch step that has routes |
| 11 Route target exists | Pass, by script |
| 12 Guarded route list ends with a fallback | Pass. `catch_up` ends with `to: merge_pr`; `delivery_gate` ends with `to: delivery_start` |
| 13 Unreachable step | Pass. In example 2, `delivery_gate`, `revert_prs` and `gate_revert` are reached only through the delivery expansion's `on_other`, which `docs/12` section 2.3 counts as reachable |
| 14 Every cycle has a route out | Pass. The test-fix-retest cycle and the catch-up-merge cycle both exit through `gate_escalate` on `attempts_exhausted` |
| 15-16 Producer and consumer contracts | Pass. `run_tests` and `retest` consume `ticket-plan` from `plan_spec`, which produces it |
| 17-19 Library references, package requirements, repository adds only | Pass. All references use one format |
| 20-22 Capabilities, decision types, attempt ceilings | Pass. `decision_type` values are namespaced |
| 23-26 Patch checks | Pass. `base_revision` present; `resume_at` resolves |
| 27-28 Step inputs, consumed artifacts | **Not checkable yet.** The new `review.run` inputs (`branch`, `base`, `context_refs`) are not in `docs/06` section 8.2 (section 7) |

**Long term** these become test fixtures in the `docs/12` section 3.3 format
(the workflow test file), so the checks run in CI rather than by hand.

---

## 7. Open questions

### Answered earlier

| Question | Answer |
|---|---|
| `run.wait` is reserved and unused | Out of the version 1 language. It returns only if a worked example needs it |
| Fix ticket or integration-branch fix (section 5) | Both are shown. The engine prefers neither; the choice is workflow content |
| `delivery.deploy` as a `parallel` branch | No: delivery is a **chain**, one `delivery.deploy` per repository, linked by routes (section 2.2) |
| The `outputs` size cap (16 entries, 256 bytes per value) | Measured by Spike 2 and kept: the largest value was 71 bytes and the most entries 5 (`docs/16` section 2) |
| `insert_after` with rewiring, or `add_step` plus `change_route` | Measured by Spike 2: a planner model authored all 20 patches in both forms; the forms did not differ reliably (`docs/16` section 3) |

### Still open

These came up while bringing in the merge and review designs. The examples make
a choice so that they are concrete; the owning document decides.

| # | Question | What the examples assume |
|---|---|---|
| 1 | Which `scm.merge` outcome means "refused: the integration branch moved since catch-up"? `docs/06` section 6.5 defines `blocked` as checks or protection | `blocked`, routed back to `catch_up` |
| 2 | Is the "about 3 refusals in a row" cap the merge step's attempt allowance, or a separate count that resets when a merge succeeds? | `limits: { attempts: 3 }` on `merge_pr`, which counts every entry, not only consecutive refusals |
| 3 | What are `review.run`'s inputs now the reviewer reads the branch itself? `docs/06` section 8.2 still has `diff_ref` | `branch`, `base` and `context_refs` (the specification and machine-check results) |
| 4 | How does an epic-scope review name its base (the default branch)? The `run` namespace has no field for it | The base is omitted at epic scope |
| 5 | How does the epic contract reach a child ticket run? | An optional `epic_contract` parameter; the expansion patches in section 2.1 do not bind it |
| 6 | Does a review of a specification artifact (section 4) keep a `diff_ref` input? | Yes |
| 7 | The schema's `Limits` has no `correction_turns`, but `docs/09` and example 1 use it | Kept in the example; fails check 1 |
| 8 | The schema requires `routes` on every step, but parallel branches carry no routes by design and chain steps get theirs from `add_chain` | Kept route-less; fails check 1 |
| 9 | `findings.all` and `findings.open` are used throughout, but `docs/09` section 8.3 lists only `findings.blocking_count` and `findings.open_count` | Kept as written |
