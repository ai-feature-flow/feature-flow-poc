# Worked examples

Status: third draft. Date: 2026-10-27. Deliverable 6 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 10 applied.** The second draft's examples failed the document's own
validation checks (A5): `apply_child_patch` did not exist, `verification_review`
was unreachable, `params.platform_repository` was undeclared, and the child
branches never passed the `ticket_id` their template requires. All four are fixed,
and every example below has been run through the validation table — section 6
records the result.

Also applied: A2 (branch completion returns to the join), A3 (expressions read no
artifact content), A4 (`run` and `findings` fields defined), A6 (`resume_at`),
A7 (`insert_after` alone, no redundant `change_route`), A10 (the epic fix path),
A11 (ordered two-repository delivery), B1 (deterministic expansion), B6 (bindings
render paths), B9 (directives as a mapping), B12 (gate choice).

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
  source: feature-flow-default:templates/ticket-delivery

parameters:
  ticket_id: { type: string, required: true }
  repositories: { type: array, required: true }

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
      diff_ref: { expr: "steps.implement.artifacts.diff" }
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
      approved: merge_pr
      rejected: { end: rejected }

  - id: merge_pr
    kind: scm.merge
    inputs:
      pr_ref: { expr: "steps.open_pr.artifacts.pr" }
    routes:
      completed: { end: completed }
      blocked: gate_escalate
      rejected: gate_merge
      attempts_exhausted: gate_escalate

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

### 1.1 What this example gets right

| Decision | How |
|---|---|
| C1: tiers come from `feature-flow.yaml` | `run_tests` passes `tier_tags` from `plan_spec.outputs`, and consumes the plan artifact to read them in its activity |
| A3: expressions read no content | `tier_tags` comes from `outputs`, not from dereferencing `artifacts.plan`. `run_tests` reads the artifact itself |
| A4: `run` fields defined | `run.repositories[0]`, `run.branch`, `run.integration_branch`, `run.head_sha` — all in `docs/09` 8.3.1 |
| A4: `findings` is a namespace | `findings.all`, `findings.open`. Not `run.findings` |
| C2: no merge method in a template | `merge_pr` passes only `pr_ref` |
| C3: no installation-specific names | `repositories` is a run parameter |
| C4: no `allowRegression` | `verify_fix` has no such input |
| C5: review feeds the findings ledger | `review.run`, `findings.triage`, `findings.evaluate_readiness` |
| C7: no policy in a template | Every gate names a `decision_type` only |
| A1: privilege from the role | `write_access` is a tightening on every step; no step sets `tools` or `capabilities` |
| B5: correction turns | `plan_spec` sets `correction_turns: 2` |
| B6: bindings render paths | `plan_path: { from_artifact: ... }`, not a hash |
| B9: directives are a mapping | `{ end: completed }`, no quoting |
| A6: resume point | `replan` routes `{ resume: true }`; the patch names `resume_at` |
| B3: loops bounded by the engine | The cycle exits through `gate_escalate` on `attempts_exhausted` |

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
      diff_ref: { expr: "run.aggregate_diff" }
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

### 2.1 Ticket expansion (B1, D2)

`tickets` starts with `branches: []`. `expand_tickets` is a `plan.replan` in
`mode: expand` — **no agent** (B1). It maps each item of the `ticket-list`
artifact to a `run.child` and returns a patch. For a three-ticket epic:

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

Four things the earlier drafts got wrong, fixed here:

- **A2: no `end: completed` on a branch.** A branch's terminal outcome returns to
  the join.
- **A1: no routes at all on a branch.** The third draft routed `blocked` and
  `replan_parent` to parent steps, which left the join and the siblings undefined
  while that ran. `blocked` is handled inside the child's own plan;
  `replan_parent` is handled by the interpreter, and siblings keep running.
- **A5: `ticket_id` is passed.** `ticket-delivery` declares it required, so a
  branch omitting it would fail check 6 at binding.
- **B4: the binding matches the patch.** The mapping binds
  `repositories: "$.repositories"` from each ticket-list item, so the generated
  patch carries the **bound value**, not `params.repositories`. The third draft
  showed the mapping and the patch disagreeing.

### 2.2 Ordered two-repository delivery (A11)

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

`proposed_by.type` is **`engine`**, not `agent` (review 11 B4): a deterministic
expansion runs no agent, so attributing it to one is wrong in the audit record.

**Order is a declared dependency, not a concurrency limit** (review 11 A2). The
third draft used a `parallel` with `max_concurrency: 1`, which is wrong twice:
with `join: all` a failure in `deliver_api` still lets `deliver_web` run, and
"in the order added" is an implicit property of a limit rather than a declared
dependency.

`add_chain` rewires `target`'s `completed` route to the new step and points every
other outcome at `on_other`. So `deliver_api` completing goes to `deliver_web`;
`deliver_api` failing goes to `delivery_gate` and `deliver_web` never runs. One
`delivery.deploy` **per repository**, chained in order — which answers the open
question from the second draft.

`environment` is gone: it was an installation-specific name, the same problem as
C3 in review 09. It comes from configuration.

### 2.3 The epic fix path (A10)

The second draft's `epic_triage` routed `completed: epic_review`, so epic
findings were triaged and re-reviewed but **never fixed**. `epic_fix` is added:
an `agent.run` with `write_access: branch` on the integration branch, which then
re-reviews. This is the seeded cross-ticket defect case (review 08, answer 4), so
it has to be in the example.

### 2.4 What this example gets right

| Decision | How |
|---|---|
| D2, B1: no hard-coded ticket ids, no agent | `branches: []`, filled by a deterministic `mode: expand` patch |
| A5: a ticket branch is a `run.child` | Each branch is `run.child` with `plan_ref` and bound `ticket_id` |
| A2: branch completion | No terminal routes on branches; the join aggregates |
| A3: join semantics | `join: all`, `max_concurrency: 2`; `any_failed` to a gate |
| A10: the epic fix path | `epic_triage` to `epic_fix` to `epic_review` |
| A11: ordered delivery | One `delivery.deploy` per repository, chained by `add_chain` |
| B12: gate choice | `delivery_gate` records a choice; guarded routes select on it |
| C6: `partial` does not route to success | `rejected: { end: partial }`, never `{ end: completed }` |
| A6: resume point | Every `plan.replan` routes `{ resume: true }` with `resume_at` in inputs |

---

## 3. Mid-run re-plan: a ticket discovers a second repository

Review 09 E7. This is the `docs/01` section 6.2 scenario: a ticket returns
`replan_parent` with a patch adding a `run.child` for the second repository to the
**epic** plan.

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
        diff_ref: { expr: "run.aggregate_diff" }
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
2. The parent applies it through the same `applyPatch` (`docs/01` 3.4).
3. `base_revision: 4` must be the parent's current revision. If the parent has
   moved on, the patch is rejected as stale and re-proposed (E3).
4. `resume_at: tickets` — execution continues at the `tickets` parallel, which
   starts the new branch.
5. Already-completed steps are untouched. `design` is protected and intact.
6. `insert_after epic_review` **rewires** `epic_review`'s `completed` route to
   `cross_repo_review` (A7). The second draft also carried an explicit
   `change_route` for the same thing, which was redundant; it is removed.

### 3.2 If the patch had touched protected content

If the patch had included `remove_step` on `design`, or a `change_route` making
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

### 4.1 What this example gets right

| Decision | How |
|---|---|
| C9: checks derive from later steps | `checks_from: [plan_review, verification_review]` |
| C9: ids are engine-generated | No `session_id` in the plan |
| **A5: `verification_review` is reachable** | `plan_review` routes `completed: verification_review`. The second draft routed it to `implement`, leaving `verification_review` unreachable — check 13 |
| D23: no check appears for the first time after submission | Both reviews can be run inside the session, before submitting |
| The trust boundary is the submission | Hooks and policy do not apply in the human's environment (`docs/01` 7.4) |

The prototype's spec session took eight rounds mostly because agent-drafted
verification requirements were accepted without review and then failed the later
plan review. `checks_from` is the mechanism that removes those round trips.

---

## 5. The epic fix path as a fix ticket (A10, alternative)

Section 2.3 fixes epic findings on the integration branch. The alternative is a
**fix ticket added by re-plan** — a `run.child` — which suits a defect that needs
its own branch, tests and review:

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

Both are valid. The integration-branch fix (2.3) is for a finding the epic review
raised on the aggregate diff; the fix ticket is for one that needs independent
verification. Which to use is workflow content (`docs/01` 1.5), not an engine
decision.

---

## 6. Validation of these examples

Review 10 A5 asks for this explicitly. Every example above was checked against
the `docs/12` section 2 table.

| Check | Result |
|---|---|
| 1 Schema conformance | Pass. All four parse as YAML; directives are mappings |
| 2 Unknown step kind | Pass. 19 distinct kinds used, all in `docs/06` |
| 3 Duplicate step id | Pass. Ids unique within each plan |
| 4 Step id looks like a kind | Pass. `run_tests`, `plan_spec`, `epic_review` — none is a kind |
| 5 Undeclared reference | Pass. `params.platform_repository` removed; every `params.*` is declared |
| 6 Unbound required parameter | Pass. `ticket_id` is passed by every `run.child` branch |
| 7-9 Expression parse, type, bounds | Pass by inspection. All expressions use declared namespaces |
| 10 Every returnable outcome routed | Pass. Verified mechanically for examples 1 and 2 |
| 11 Route target exists | Pass. `apply_child_patch` removed; every target resolves |
| 12 Guarded list has a fallback | Pass. `delivery_gate` ends with an unguarded `to: delivery` |
| 13 Unreachable step | Pass. `verification_review` is now reachable from `plan_review`. `delivery_gate` is reachable only through the delivery expansion's `on_other`, which `docs/12` 2.3 counts as reachable |
| 14 Every cycle has an exit | Pass. The test-fix-retest cycle exits via `gate_escalate` |
| 15 Contract compatibility | Pass. `run_tests` consumes `ticket-plan` from `plan_spec`, which produces it |
| 16 Producer declares the consumed artifact | Pass |
| 17-19 Library, requires, repository adds only | Pass. All refs use one format |
| 20-22 Capabilities, decision types, ceilings | Pass. `decision_type` values are namespaced |
| 23-26 Patch checks | Pass. `base_revision` present; `resume_at` resolves |
| 27-28 Step inputs, artifacts | Pass |
| A2 Branch terminal routes | Pass. No branch routes a terminal outcome or uses a directive |

**Mechanically verified:** examples 1 and 2 were parsed and checked by script —
every route target resolves to a declared step id or a directive, and every step
routes every outcome its kind can return. Examples 3 and 4 are fragments of those
plans and were checked by hand.

**Long term** these become test fixtures in the `docs/12` 3.3 format, so the
checks run in CI rather than by hand.

---

## 7. Open questions

None open. Every question from the earlier drafts was answered in review 10 or 11:

| Question | Answer |
|---|---|
| `run.wait` is reserved and unused | Removed from the language per review 10. It returns only if a worked example needs it |
| Fix ticket versus integration-branch fix (section 5) | Both are shown. **The engine prefers neither** — the choice is workflow content (review 11 D) |
| `delivery.deploy` as a `parallel` branch | Superseded by A2: delivery is a **chain**, not a parallel. One `delivery.deploy` per repository, linked by routes |
| One `delivery.deploy` re-entered, or one per repository | One per repository (review 10, answer) |

**Two measurements remain**, assigned to spikes rather than open as design
questions: the `outputs` cap (Spike 2, review 11 C1) and `insert_after` rewiring
versus `add_step` plus `change_route` (Spike 1 or 2, review 11 C2).
