# Engine step catalogue

Status: revised 2026-10-31 for the merge design (`docs/20`), the code review
design (`docs/19`), the findings ledger position (`docs/17` section 6.5) and the
design requirements from the spikes (DR1 to DR13, listed in `docs/08` section
1.3).

This document gives the contract of every step kind the engine provides. Related
documents:

| Document | What it holds |
|---|---|
| `docs/01` section 7 | How the steps are grouped, and the reasoning behind the groups |
| `docs/04`, `docs/05` | The provider interfaces and contracts these steps call |
| `docs/09` section 4.2 | Which outcomes each step kind may return, for routing and validation |

**The principle.** The engine provides mechanisms and guardrails; the workflow
decides. The engine makes things possible and safe; it does not dictate how a
workflow is shaped. So the number and names of branch levels, the base each
review compares against, and where delivery to `main` happens are workflow
choices that these steps support, not engine rules.

**Scope.** Every step has a full contract. A thin step (a direct wrapper over one
provider call) has a short contract and says so. A step that carries engine logic
states that logic.

---

## 1. How to read a step contract

| Element | Meaning |
|---|---|
| Kind | The step's identifier in a plan |
| Form | Local (pure code inside the workflow), Activity, or Child workflow (`docs/01` section 3.2, the three execution forms) |
| Scope | The level the step runs at: typically ticket, epic, project or delivery, but the levels and their names come from the workflow. `any` means the step does not care. A run at one level starts the runs of the level below (section 4.1) |
| Inputs | What the plan supplies. Artifact references, never inline content |
| Requires | Provider capabilities. Checked when the run starts, and again if `feature-flow.yaml` changes (`docs/01` section 5.1.2) |
| Outcomes | Named results the plan routes on (section 1.1) |
| Idempotency | How an interrupted step detects what it already did |
| Limits | Default timeout, heartbeat and attempt allowance (`docs/01` section 3.7, attempt budgets) |
| Error overrides | Where the step reclassifies a provider error because it knows the intent |
| Artifacts | In and out |
| Audit | Timeline events written |

### 1.1 Outcome vocabulary

Outcomes are what a plan routes on. A step returns exactly one.

| Outcome | Meaning |
|---|---|
| `completed` | The step did its work |
| `blocked` | The step cannot proceed; something external must change |
| `failed` | The step did its work and the work was wrong or rejected |
| `attempts_exhausted` | The step's attempt allowance is spent. Produced by the interpreter, never a silent failure |
| `replan_requested` | The step produced a plan patch |
| `replan_parent` | A child run asks its parent to re-plan |
| `partial` | Some of the work happened (delivery) |
| `approved` / `rejected` | A human decision. `scm.merge` also returns `rejected` when the human it asked to merge declines (section 6.5) |
| `not_approved` | `policy.auto_approve` did not approve; a human gate follows |
| `findings_open` | Blocking findings remain |
| `ready` | The merge bar is met: no unresolved blocking finding, and every finding fixed or declined with a recorded reason (decision D8) |
| `any_failed` | A `parallel` join was not satisfied |
| `base_moved` | `scm.merge` only: the target branch moved since the ticket caught up with it, so the merge was refused (section 6.5) |

Every step that is not local and pure must route `attempts_exhausted`
(`docs/09` section 4.2). The outcome lists in this document include it where it
applies.

### 1.2 Rules every step follows

| Rule | Detail |
|---|---|
| Idempotency key | `hash(runId, stepId, stableInputs)`. Stable inputs exclude anything that changes per attempt |
| Manifest | A step reads `feature-flow.yaml` from its branch when it starts and records the hash of the resolved manifest (`docs/01` section 5.1.2) |
| No human waits inside an activity | A wait for a human is a durable wait in the workflow, never an activity timeout. Risk R8 in `docs/08` (runs stall on human latency) |
| Cost | Every step records tokens, model and compute time (`docs/01` section 5.5) |
| Attempts | Counted by `docs/01` section 3.7: infrastructure failures are free; a resume is free only if the agent made progress |
| Limits are configured hierarchically | Every limit (attempts, timeouts) has an engine default, overridable by the workflow, then a child workflow, then the step. The defaults in this document are the engine defaults. Policy ceilings still apply (floor F4: a run sets budgets within the configured ceiling, never above it) |
| Ceilings on every execution | Attempt ceilings apply every time a step runs, including when a loop routes back into it (DR10). Without this, Spike 3 saw a loop with no agent run its fix step 213 times in 15 seconds (`docs/18` section 4) |
| Re-entry | Routing to a step that already has an outcome runs it again (DR12). This is what makes loops work. Re-running clears the step's current outcome, outputs and artifacts, so a route condition never mistakes a stale value for the latest. The previous execution's results stay readable as `steps.<id>.previous`, and `steps.<id>.executions` gives the execution count |
| Unexpected success | A provider maps every documented success response to an outcome, including empty ones such as GitHub's 204 for a no-op merge. An unmapped success is a work error, not an infrastructure error, so it cannot be retried forever (DR8) |
| No push credential for agents | An agent commits in its own sandbox; the engine detects the commits and pushes them (DR13; section 2.1). An agent's code host credential is read-only and per step, scoped to the run's repository plus any other repositories the workflow declares the agent may read (for example to verify a contract). Model credentials never enter the agent's environment (DR1) |
| Audit | Every step writes a timeline event on entry and on outcome |

---

## 2. Agent steps

### 2.1 `agent.run`

Runs an agent with a role and a task inside a sandbox. Under pattern C (`docs/01`
section 1.4: the agent runtime runs in the sandbox, and its hooks call back to the
engine before and after each tool call), the engine sees every tool call.

| Element | Detail |
|---|---|
| Form | Activity (agent worker) |
| Scope | any |
| Inputs | `role`, `task_ref` (artifact), `write_access` (optional, may only **tighten** the role's value), `limits`, `resume_strategy`, `push` (default true), `on_uncommitted` (`commit`, the default, or `fail`) |
| Requires | Sandbox `Os[target]`, agent runtime, VCS provider (to detect and push commits). `Hooks` optional; `SessionResume` optional |
| Outcomes | `completed`, `blocked`, `failed`, `attempts_exhausted`, `replan_requested` |
| Idempotency | Key from run, step and stable inputs. On retry: re-attach to the sandbox by key (`AttachAsync`), else create one; resume the agent session by its stored reference |
| Limits | `start_to_close` from the plan (default 4h), heartbeat 30s, attempts 3, enforced on every execution (DR10) |
| Error overrides | An agent that ran out of time is a **work** failure (error code `timeout`), not infrastructure. The provider may classify it as infrastructure; the step overrides |
| Artifacts | In: task, context. Out: transcript, produced artifacts, session reference, and the record of files the agent opened |
| Audit | Step start, each resume, outcome, cost |

Contract details:

- **Resume after a crash** (DR9; `docs/01` section 3.6):
  - The agent runs in its own sandbox, separate from the agent worker. A worker
    restart, including every deploy, costs nothing: the new worker reconnects.
  - **If the sandbox survived**, the agent's conversation is reloaded from the
    session reference and uncommitted files are kept. Resume must not depend
    on the crashed worker's memory. Spike 3 found that it did, so a resumed
    step restarted its agent session and redid the work (`docs/18` section 2,
    injection I1).
  - **If the sandbox is lost**, the step restarts in a new sandbox from the
    hidden work-in-progress reference (below), with a **fresh agent session**.
    The agent sees the pushed commits and a note that the previous attempt was
    interrupted. The engine does not save the conversation mid-step, and there
    are no snapshots of uncommitted work; revisit only if real runs show sandbox
    loss is common.
  - The full transcript is kept as the step's artifact at the end of the step,
    for audit.
  - The step records, per attempt, the commit it resumed from and the session
    reference it used.
- **Progress**, for the attempt budget, comes from `GetProgressAsync`: a new
  commit, or growth of the agent session. After 3 consecutive resumes with no
  progress, the next counts as a restart (`docs/01` section 3.7).
- **The agent commits, the engine pushes** (DR13). The agent commits locally as
  often as its prompt or skill says ("commit after each meaningful change"). It
  holds no push credential: GitHub cannot limit a token to particular branches,
  so withholding the credential is the only reliable control.
  - **Detection is through the VCS provider**, not the agent runtime. At each
    check-in on a running step (about every 30 seconds) the engine asks the VCS
    provider for the working copy's current revision. If it moved, the engine
    fetches the new commits and pushes them with its own credential. A
    runtime's hooks may trigger an immediate push, but nothing depends on them,
    so every runtime works the same way.
  - **While the step runs, pushes go to a hidden reference**, for example
    `refs/ff/wip/<ticket>`. It is not a branch, so it does not trigger CI and
    does not expose half-finished work to pull requests, reviewers or a
    sibling's catch-up.
  - **When the step succeeds, the engine moves the ticket branch** to the final
    commit, once. This is a push to the ticket's own branch, not a merge. A
    failed step's commits stay on the hidden reference, so a retry continues
    from them.
  - **Uncommitted changes at the end of the step** are committed by the engine
    with the message "left uncommitted by the agent", so nothing is lost
    silently. `on_uncommitted: fail` makes the step fail instead.
  - **An optional `checkpoint` tool**, offered through the engine's MCP server
    (the one planned for interactive sessions), lets an agent commit and have
    the engine push without knowing which version control system it is in. It
    is a convenience; the engine's detection is the backstop.
  - Pushing follows the step's write access. A role with no write access pushes
    nothing, and commits made anyway fail the step (the after-step check).
    `push: false` switches pushing off, for scratch steps.
- **The engine records what the agent opened**, for every agent step: from the
  runtime's hooks, or from its streamed tool-call output (Claude Code and
  Copilot CLI can both stream tool calls), or "unknown" if neither is
  available. This is a mechanism only; the engine never fails a step on it.
  `review.run` publishes it as a coverage output (section 8.2).
- **Write access is enforced twice**: by the sandbox (what the role can reach) and
  by the hook endpoint on each tool call. A reviewer role cannot change the
  repository. Per-tool-call enforcement needs the runtime's pre-tool-call hook:
  - Claude Code fired all its hooks (session start, before and after each tool
    call, stop) on Windows and macOS (`docs/16` sections 4 and 12).
  - Copilot CLI's before- and after-tool-call hooks did not fire with a
    third-party model (untested with a first-party one). Without them the
    fallback in `docs/08` section 2.5 applies: sandbox-enforced write access, a
    diff check after the step, and cost from the runtime's own usage output.
- **`write_access`, `tools` and `capabilities` come only from the role.** A step
  may **tighten** `write_access` (`repository` to `branch`, `branch` to `none`)
  and never relax it; validation rejects a move the other way. Why: an agent may
  not replace a role, but it may change inputs or insert a new `agent.run`, so
  if these were inputs it could grant itself `repository` access and any tool.
  The step still states `write_access`, so the floor stays visible in the plan.
- **The result comes from `GetResultAsync`**, not from the session-end hook. The
  hook is only a signal.

### 2.2 Write access is a mechanism, roles are content

`write_access` (`none`, `branch`, `repository`) is enforced by the engine: by the
sandbox, and by the hook endpoint on each tool call. **Roles are workflow
content** (`docs/01` section 1.5: the engine provides mechanisms and
guardrails, the workflow decides). So there is no separate review step kind for agents: a reviewer
is an agent with `write_access: none`, which keeps the floor visible in the plan.
`review.run` (section 8.2) runs such an agent with engine checks around it.

---

## 3. Human steps

### 3.1 `human.gate`

Waits for a human decision.

| Element | Detail |
|---|---|
| Form | Local policy check, then a durable wait in the workflow |
| Scope | any |
| Inputs | `gate_id`, `prompt_ref`, `context_refs` |
| Gate | `decision_type`: a namespaced name, for example `ticket.merge_approval` |
| Requires | Notifications (optional, for the reminder) |
| Outcomes | `approved`, `rejected` |
| Idempotency | By `gate_id`. A decision already recorded is not asked again |
| Limits | **No timeout.** Human waits are unbounded (assumption A3: a run may sit at a gate for days). Reminders are timers, not timeouts |
| Error overrides | None |
| Artifacts | In: what the human is being asked about. Out: the decision record |
| Audit | Gate opened, reminded, decided, with actor and time |

Contract details:

- **The gate names a decision type; who may approve comes from the
  organisation** (decision L12). Who may approve, how many approvers, whether an
  agent or chat counts, and auto-approval come from `policy.approval` in the
  organisation layer (`docs/07` section 4). That is organisation governance,
  tied to identities, and is the only policy a template may not carry. A
  template that carried it could relax an organisation's approval rule.
  Everything else in a template is workflow policy: a step's parameters are the
  default policy, overridable down the levels. An unknown `decision_type` is a blocking validation error at run
  start.
- The decision arrives as a **Temporal Update** carrying the gate id and plan
  revision. The workflow's validator rejects a stale or invalid request
  (`docs/01` section 5.4, the control plane).
- The API authenticates the human, evaluates the **`approval`** policy, and writes
  the decision record before sending the update; the workflow trusts the API
  (decision DP8). The separate `steering` policy covers amendments, overrides,
  extra attempts and abort, not gate decisions.
- Chat approvals arrive the same way: the chat provider's inbound endpoint turns
  the answer into an API decision call.

### 3.2 `human.session`

An interactive session: prepare a workspace, wait for a submission, validate it.

| Element | Detail |
|---|---|
| Form | Activity to prepare, then a durable wait |
| Scope | any |
| Inputs | `purpose`, `context_refs`, `checks_from` (ids of later steps whose checks to offer) |
| Requires | Session surface |
| Outcomes | `completed`, `rejected` |
| Idempotency | By the engine-generated session id |
| Limits | No timeout on the wait |
| Error overrides | None |
| Artifacts | In: context. Out: the submission |
| Audit | Session prepared, checks run, submitted, with the human as the accountable actor |

Contract details:

- The session surface **only** prepares the workspace and returns how the human
  reaches it. Submission and checks are API actions.
- **Pre-submit checks** let the human run, on demand, the same checks the later
  gated steps will apply, so no check appears for the first time after
  submission (`docs/01` section 7.4; risk R17 in `docs/08`).
- **Parity** means the pre-submit check runs with the same brief, the same tools,
  the same model and the same commit as the gated step. "Identical input" is not
  enough for a review, because the reviewer explores the repository (section
  8.2). Spike 3 evidenced parity for a diff-only reviewer, and noted that this is
  close to true by construction, because the pre-submit tool calls the gated
  step's own input builder (`docs/18` section 5).
- **`checks_from` names step ids, not check names.** The session offers the
  checks of the named later steps, derived from the plan, so the offered set
  cannot drift from what will be applied. The session id is engine-generated; a
  plan does not carry one.
- **"Merge when ready".** A session may offer a tool that asks the engine to run
  the merge step (section 6.5). The tool never merges itself.
- Hooks and policy do not apply in the human's environment. The trust boundary is
  the submission.

### 3.3 `policy.auto_approve`

| Element | Detail |
|---|---|
| Form | Local, pure |
| Scope | any |
| Inputs | The gate's policy object, run state |
| Requires | None |
| Outcomes | `approved`, `not_approved` |
| Idempotency | Pure: same inputs, same result |
| Limits | None |
| Audit | Recorded as a decision with actor `policy` |

Contract detail: by default it **cannot** approve a merge into a default branch.
That is governed by the default-branch setting (floor F1, `docs/07` section 2):
by default the engine may not merge into the default branch, so a human does. F1
is now a configurable setting with that safe default; see section 6.5.

---

## 4. Composition steps

### 4.1 `run.child`

Starts a child run on the same interpreter with a scoped plan: the run of the
level below, such as a ticket inside an epic or an epic inside a project. The
levels come from the workflow, at any depth.

| Element | Detail |
|---|---|
| Form | Child workflow |
| Scope | Any level that has a level below it (typically project, epic or delivery) |
| Inputs | `child_scope`, `plan_ref`, `repositories`, `protected` |
| Requires | Whatever the child plan requires |
| Outcomes | `completed`, `failed`, `replan_parent`, `attempts_exhausted` |
| Idempotency | By run id and child index. A retry adopts the existing child run |
| Limits | No `start_to_close`; the child has its own |
| Artifacts | In: the child plan. Out: the child's artifacts, its findings ledger, and any patch for the parent |
| Audit | Child started, child outcome |

Contract details:

- **`completed` only when the child reached `{ end: completed }`.** A run that
  stops without reaching an `end:` route never reports `completed` (DR6). Spike 2
  showed why: a merge step's unrouted failure ended a run that looked
  successful (`docs/16` section 5). A child that stops with no step ready and
  no `end:` route ends with run status `incomplete`, and its parent's
  `run.child` sees it as `failed`.
- **`replan_parent`** carries a plan patch for the parent, applied through the
  same `applyPatch` path as any re-plan (`docs/01` section 3.4). This is how
  discovery inside a ticket changes the epic, or inside an epic changes the
  project.
- **The child's findings ledger is returned** so that the parent level's review
  includes every child's ledger in its prior findings (DR11; section 9.4,
  position L4).

### 4.2 `run.wait` — reserved, not in version 1

Not in the version 1 workflow language. `parallel` with `run.child` branches
covers every case the worked examples raise (`docs/13`). The contract is kept so
the kind can return if an example needs it.

Waits for one or more child runs.

| Element | Detail |
|---|---|
| Form | Durable wait |
| Scope | epic or delivery |
| Inputs | `child_refs`, `join` (`all`, `any`, `n`) |
| Outcomes | `completed`, `any_failed` |
| Idempotency | By child run ids |
| Limits | No timeout |

### 4.3 `parallel`

Fan-out and join. Not a step that does work; a plan construct the interpreter
executes (`docs/01` section 3.3.1).

| Element | Detail |
|---|---|
| Form | Interpreter |
| Scope | any |
| Inputs | `branches`, `join` (`all`, `any`, `n`), `max_concurrency` |
| Outcomes | `completed`, `any_failed` |
| Idempotency | Each branch has its own key |
| Limits | `max_concurrency` bounds branches in flight |

Contract details:

- **A ready branch starts as soon as a slot frees**, not when a whole batch has
  finished (DR7). Spike 3 found that waiting for the batch blocked a ticket on
  its siblings by construction. Interpreter changes like this are versioned for
  replay (Temporal `Workflow.Patched`).
- A failed branch does not cancel or stall its siblings; the join reports
  `any_failed` only after every branch has finished (`docs/18` section 2,
  injection I3).
- A re-plan may add branches to a `parallel` step that has not started, and may
  not remove a protected branch.

---

## 5. Planning steps

### 5.1 `plan.replan`

An agent proposes plan changes; the interpreter applies them while the run
continues.

| Element | Detail |
|---|---|
| Form | Activity (agent), then local `applyPatch` |
| Scope | any |
| Inputs | `reason`, `context_refs`, `constraints` |
| Requires | Agent runtime, sandbox |
| Outcomes | `replan_requested`, `failed`, `attempts_exhausted` |
| Idempotency | By run id and plan revision |
| Limits | Attempts 2 |
| Artifacts | In: current plan, context. Out: the patch, and the new plan revision |
| Audit | Patch proposed, validated, applied or rejected, with the diff |

Contract details:

- The patch goes through `applyPatch` (`docs/01` section 3.4), which checks:
  known step kinds, present capabilities, **protected steps not removed or
  weakened** (floor F3), routes resolving to steps that exist, and attempt
  budgets within the policy ceilings (floor F4). The checks are listed in
  `docs/12` section 2.
- A rejected patch is recorded, not silently dropped.

### 5.2 `plan.validate`

| Element | Detail |
|---|---|
| Form | Local, pure |
| Scope | any |
| Inputs | `plan_ref` |
| Outcomes | `completed`, `failed` |
| Idempotency | Pure |
| Audit | Validation result |

---

## 6. Source control steps

All write a hidden idempotency marker on each remote object they create, and
adopt an existing object on retry (decision S4).

**Two providers.** Version-control operations belong to the **VCS provider**:
the current revision of a working copy, fetching commits from a sandbox's working
copy, pushing to a branch or reference (refused if the target moved), and
building merge or squash commits. The **code host provider** keeps pull requests,
comments, protection and other hosting APIs. Version 1 implements Git only; the
VCS interface stays neutral so another version control system could be added.

### 6.1 `scm.create_branch`

| Element | Detail |
|---|---|
| Form | Activity (orchestration) |
| Scope | any |
| Inputs | `repository`, `branch`, `from_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repository, branch. On retry, `FindByMarkerAsync` then adopt |
| Limits | 30s, attempts 3 |
| Error overrides | `conflict`, when the branch exists at a different commit, is a **work** outcome: reconcile, do not retry |
| Audit | Branch created or adopted |

### 6.2 `scm.merge_base_forward`

**Catch-up**: merges the parent branch (the branch the work was taken from and
will merge back into) into the work branch, so the work is tested against what it
will merge into (section 6.5). For example the epic branch into a ticket branch,
or the project branch into an epic branch. The text below uses a ticket as the
example.

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | Any level below the top |
| Inputs | `repository`; `branch`: the parent branch, merged **from**; `into`: the work branch, merged **into** |
| Requires | VCS provider (merge and push); code host `PullRequest` |
| Outcomes | `completed`, `blocked` (conflict), `attempts_exhausted` |
| Outputs | `branch_changed` (boolean): true if the work branch did not already contain the parent branch, so the merge changed it |
| Idempotency | Safe to repeat. A second run finds nothing to merge and reports `branch_changed: false` |
| Limits | 30s, attempts 3 |
| Error overrides | A conflict is a **work** outcome (`blocked`), routed to an agent step that resolves it, **not to a human first** |
| Audit | Merge attempted, `branch_changed`, conflict raised |

Contract details:

- **Re-test only when the branch changed.** The plan routes `completed` on
  `branch_changed`: true goes to the tests, false straight to the merge. Spike 3
  saw both cases and checked each against `git merge-base --is-ancestor`
  (`docs/18` section 2, check C2).
- **A no-op merge is `completed`.** GitHub answers a merge with nothing to do
  with 204 and an empty body. The provider maps that to `completed` with
  `branch_changed: false` (DR8). Read as an infrastructure error, it would be
  retried forever.
- **Conflicts go to an agent.** In Spike 3 a real conflict was resolved by an
  agent with no human involved, keeping both sides. The resolved branch then went
  back through tests, review and the merge gate, so resolution code was not merged
  unreviewed (`docs/18` section 2, check C3).

### 6.3 `scm.open_pr`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `branch`, `base`, `title`, `body_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repository, branch, base. Marker in the PR body |
| Limits | 30s, attempts 3 |
| Artifacts | In: body. Out: PR reference |
| Audit | PR opened or adopted |

### 6.4 `scm.update_pr`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `pr_ref`, `title`, `body_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Last write wins; safe to repeat |
| Limits | 30s, attempts 3 |

### 6.5 `scm.merge`

Merges a pull request's branch back into its parent: the branch it was taken
from, one level up. Typically a ticket branch into its epic branch, an epic
branch into its project branch, or a project branch into the default branch; the
levels, and where delivery to `main` happens (once at the end, or several times
along the way), are the workflow's choice. Either the engine performs the merge,
or it asks a human to merge on the code host and waits. **There is no merge
lock** (`docs/20`, the merge design).

| Element | Detail |
|---|---|
| Form | Activity to merge or to raise the intervention; a durable wait while a human merges |
| Scope | any |
| Inputs | `pr_ref`; `engine_can_merge` (boolean: true, the engine merges; false, a human merges); `squash` (boolean: true for a squash merge, false for a plain merge) |
| Requires | VCS provider (build the merge or squash commit, push it). Code host `PullRequest`. `MergeQueue` when the target requires one |
| Outcomes | `completed`, `base_moved`, `blocked` (required checks not passed), `rejected`, `attempts_exhausted` |
| Idempotency | Key from run, step, PR. If the target already contains the merge, or the human's merge is already on the code host, the merge already happened: `completed` |
| Limits | 30s for the push. A wait for a human or a merge queue is a durable wait, not an activity timeout. Attempts 3 by default, which bounds repeated `base_moved` refusals (see below) |
| Error overrides | A push refused because the target moved is a **work** outcome, `base_moved`, never an infrastructure retry: the ticket must catch up and re-test first. Required checks not passing is a **work** outcome, `blocked`. A code host protection refusal of an allowed engine merge raises an intervention (see below) |
| Audit | Merge attempted, `squash`, who merged (engine or human), approver, and each `base_moved` refusal |

Contract details:

- **Settings in a typical three-level workflow** (workflow policy; a workflow
  may use fewer or more levels):

  | Merge | `squash` | `engine_can_merge` |
  |---|---|---|
  | Ticket branch into epic branch | true (no need to keep every commit) | true |
  | Epic branch into project branch | false (each ticket's commit stays visible) | true |
  | Project branch into the default branch | false | false by default (the default-branch setting, below) |

- **The flow when the engine merges.** Tickets merge the way developers do:
  1. Catch up: `scm.merge_base_forward` (section 6.2).
  2. Re-test if catch-up changed the branch.
  3. Merge. If the target moved since step 1 (a sibling merged first), the
     merge is refused with `base_moved`, and the plan routes back to step 1.

  Repeated refusals escalate to a human, so a slow ticket cannot be starved by
  faster siblings. The bound is the step's attempt allowance, default 3. It
  counts every attempt, including re-entry after a conflict was resolved
  (DR10), and like every limit it can be overridden by the workflow, a child
  workflow and the step (section 1.2). `attempts_exhausted` routes to a human
  gate.
- **Nothing untested can land.** The engine builds the merge commit, or the
  squash commit, from exactly the commits that were tested, and pushes it to the
  target. Git refuses a push onto a branch that moved since it was fetched,
  which is what produces `base_moved`. This needs no branch protection settings
  and no extra permissions.
- **Squash merges and the pull request.** After a plain merge the code host
  marks the pull request merged, because its commits reach the base. After a
  squash it does not (its commits are not in the base), so the engine closes the
  pull request with a comment linking the squash commit.
- **When a human merges** (`engine_can_merge: false`). The step raises an
  intervention (a human decision request) and waits. The human merges on the
  code host; the engine detects the merge there and the step returns
  `completed`. If the human declines, the step returns `rejected`.
- **When the code host refuses an allowed engine merge**, for example because
  branch protection on `main` requires an independent review, that is an ordinary
  intervention: the engine asks a human to act and waits, exactly as when
  `engine_can_merge` is false. No separate approval rule is implied by the
  setting.
- **The default-branch setting (floor F1).** By default the engine may not merge
  into the default branch, so `engine_can_merge` must be false there. Whether a
  workflow lets the engine merge into `main` is the workflow's choice
  (`engine_can_merge` is workflow policy), within the organisation's cap. F1 used to
  be a fixed floor ("a human must approve every merge into the default
  branch"); it is now a configurable setting with that safe default.
  - It is set by the organisation, then the repository, then the workflow, and
    may be relaxed, for example for a proof-of-concept repository. The
    organisation can cap it ("never the engine") so lower levels cannot relax
    it.
  - **Nothing an agent can edit can change it.** The repository's value is read
    from its configuration on its default branch, never the ticket branch, and a
    plan patch during a run cannot change it.
  - The engine enforces it when the plan is validated (`docs/12` check 33) and
    again at merge time, reading the default branch from the code host (DR2).
  - `policy.auto_approve` cannot approve a default-branch merge while the
    setting is at its default.
- **Only the engine merges, unless a human does.** An engine merge is a workflow
  step, so after a crash it resumes and completes exactly once (proved in Spike
  1, `docs/15`). Agents hold no push credential (DR13). A human session's "merge
  when ready" tool only asks the engine to run this step (section 3.2).
- **Code host backstop.** Rules on epic and project branches allow only the
  engine's identity to update them, so an engine bug is also caught by the code
  host. On the default branch, code host protection requiring independent review
  stays as the final fallback, and the engine never holds a bypass. This
  protection is configured by an identity the engine does not hold; the engine
  only checks it exists (DR3).
- **Merge queue.** When a target requires a queue, the step enqueues
  (`EnqueueMergeAsync`) and waits by polling `GetPullRequestAsync` or by inbound
  event. A merge queue is also the managed answer if contention between tickets
  ever becomes real (`docs/20`).
- **What the lock bought, and its replacement does not.** Under a lock a ticket
  never re-ran its tests because it lost a race. With two or three tickets in
  flight, races are rare and a re-run costs one CI run. The lock's failure mode
  is gone: in Spike 3 a stalled lock holder delayed every other merge
  (`docs/18` section 2, check C7).

### 6.6 `scm.revert`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `pr_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, PR |
| Limits | 30s, attempts 3 |

Contract detail: serves the revert choice after a partial delivery (`docs/01`
section 7.3). It opens revert PRs and **always gates**: it never merges
automatically after approval. Reverts usually target a default branch, where
the default-branch setting (floor F1, section 6.5) means a human merges anyway.

---

## 7. CI and test steps

### 7.1 `ci.trigger`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `tier`, `commit_sha` |
| Requires | CI `Trigger` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repository, tier, commit |
| Limits | 30s, attempts 3 |
| Audit | Run triggered |

### 7.2 `ci.wait_checks`

Waits for every check on a PR, **including checks that register late**.

| Element | Detail |
|---|---|
| Form | Durable wait with polling, or inbound event |
| Inputs | `repository`, `commit_sha`, `required_checks` |
| Requires | CI `Trigger`, or code host `CheckStatus` |
| Outcomes | `completed`, `failed`, `blocked`, `attempts_exhausted` |
| Idempotency | Safe to repeat |
| Limits | Step-defined timeout; heartbeat via the poll |
| Error overrides | A failing check is a **work** outcome, not infrastructure |
| Audit | Checks waited, final status |

Contract details:

- The step polls `GetRunStatusAsync` **inside one long-running activity**, not one
  activity per poll, and never calls a blocking wait. Spike 1 measured a
  60-second polled step at 11 history events, against about 120 activities for
  one activity per poll (`docs/15` section 6.2; risk R20 in `docs/08`, polling
  inflating history).
- It is kept separate from `wait.for_state` because it carries specific
  semantics: late-registering checks, required check names, and a grace period
  before deciding there is no CI.

### 7.3 `ci.read_results`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `run_ref` |
| Requires | CI `Trigger` |
| Outcomes | `completed`, `partial`, `attempts_exhausted` |
| Idempotency | Safe to repeat |
| Limits | 30s, attempts 3 |
| Artifacts | Out: structured results, log reference |

### 7.4 `test.run_tiers`

Runs the repository's declared test tiers.

| Element | Detail |
|---|---|
| Form | Activity (agent worker, because it needs a sandbox) |
| Inputs | `repository`, `tier_tags` |
| Requires | Sandbox `Os[target]`. `Docker` for Docker-dependent tiers |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repository, tiers, commit |
| Limits | Step-defined, heartbeat required |
| Error overrides | A test failure is a **work** outcome |
| Artifacts | Out: results, logs |
| Audit | Tiers run, results |

Contract details:

- **One step runs a set of tiers.** Tiers with the same execution target share a
  sandbox; results are reported per tier. A tier delegated to CI runs through the
  CI provider **inside the same step**, so the plan sees one outcome for the set.
- **The plan selects tiers by tag; it does not list them.** Tiers and their
  execution targets are repository facts in `feature-flow.yaml`, read when the
  step starts (`docs/01` section 5.1.2), so a tier change mid-run is picked up. A
  plan that listed them would copy repository facts and go stale. A tag with no
  matching tier is a validation error at step start.
- **A Docker-dependent tier defaults to CI delegation** (decision Q2 in
  `docs/03`). Spike 2 dispatched such a tier to the CI workflow the manifest
  names; the step returned in 22.7 seconds without waiting, and polling then saw
  the run succeed (`docs/16` section 2, check R5).
- Commands are started and polled, so a long tier survives a worker crash when the
  sandbox does.

### 7.5 `test.run_composition`

Runs several repositories together as services, for integration tests.

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repositories`, `composition_ref`, `tiers` |
| Requires | Sandbox `Docker`, or CI delegation |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repositories, commit set |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the composition (a compose file referenced from `feature-flow.yaml`). Out: results |

---

## 8. Analysis and review steps

### 8.1 `analysis.run`

A machine check: runs a static analyser. Its results are given to the reviewer
(section 8.2).

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `analyser`, `baseline_ref` |
| Requires | Sandbox, or CI delegation |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, repository, commit |
| Limits | Step-defined, heartbeat required |
| Error overrides | New issues are a **work** outcome |
| Artifacts | Out: results (SARIF or equivalent) |

Contract detail: the analyser comes from `feature-flow.yaml`. The step fails a
change that **adds** issues, not one that already has them.

### 8.2 `review.run`

Reviews a branch against its parent branch (the branch it was taken from and will
merge back into) and a specification. The design is in `docs/19` (code review
design).

The step calls the **review provider**, the plug point for anything that reviews
a change and returns structured findings. In version 1 the standard reviewer is
an agent on a read-only checkout, built from the sandbox and agent runtime
providers; the contract details below describe it. Others, such as an external AI
review service or a code host's pull request review, can plug in alongside, and
their findings go into the same ledger.

| Element | Detail |
|---|---|
| Form | Activity. For the standard reviewer: agent worker, an agent with a reviewer role and `write_access: none`, wrapped by engine checks |
| Scope | Any level |
| Inputs | `scope`; `branch` (under review); `base` (its parent branch); `spec_refs` (the specification documents to review against: acceptance criteria, epic contract, repository conventions); `machine_results` (results of the machine checks); `checks` (the review angles to run); `prior_findings`; `require_coverage` (`none`, the default; `all`; or a list of paths). A review of a specification document rather than code keeps a document reference input (`diff_ref`) instead of `branch` and `base` |
| Requires | Review provider. The standard agent reviewer needs a sandbox and an agent runtime; any runtime can review |
| Outcomes | `completed`, `findings_open`, `failed`, `attempts_exhausted` |
| Outputs | `coverage`: changed files, files read, files not read, and whether the record is verified |
| Idempotency | Key from run, step, scope, branch head and base commits (or the document reference) |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the specification, machine-check results, prior findings. Out: structured findings, the record of files the reviewer opened, and which model authored and which reviewed |
| Audit | Review run, angles run, files opened, authoring and reviewing models, findings recorded |

Contract details:

- **Machine checks first.** Build, tests, linters, analysers, formatting, and test
  coverage of the changed lines run before the review. The reviewer gets their
  results instead of rediscovering them.
- **The reviewer explores; nothing is pre-loaded.** It is told the branch, the
  base and the list of changed files. It reads the diff itself (`git diff`),
  opens any file it needs (callers, contracts, tests), and may build and run
  tests. It cannot change the branch. There is no diff in the prompt, so no
  prompt size limit. Spike 3 showed why: a diff silently cut to 6,000
  characters hid the bug from the reviewer (`docs/18` section 4).
- **Against a specification**: the ticket's acceptance criteria, the epic's
  contract, and the repository's own conventions and architecture notes.
- **Separate angles**, each a focused pass: correctness, design and structure,
  tests (would they fail if the code were wrong?), security, and conformance to
  the specification. Which angles run at which level is not yet decided.
- **Bug findings carry evidence.** Where it can, the reviewer writes a throwaway
  test that fails because of the bug. A demonstrated finding blocks. A reasoned
  finding (no failing test) is labelled as reasoned and ranked lower; how it is
  triaged is not yet decided. Demonstrated findings do not change between runs,
  so evidence is the main defence against unstable verdicts (DR4: review
  verdicts are unstable with a real model).
- **Reviewer model: workflow policy, not an engine rule.** A different, ideally
  stronger, model than the author's is recommended, but other models may not be
  available. The model comes from the role mapping in the organisation layer
  (`docs/09` section 11.1). The engine records which model authored and which
  reviewed, so the choice is visible.
- **Coverage is a mechanism, not an engine rule.** The engine records what the
  reviewer opened, from hooks or from the runtime's streamed tool-call output,
  or "unknown" if neither is available (section 2.1). It publishes this as the
  `coverage` output; it never fails a step on coverage itself. The workflow
  decides what coverage it needs:
  - `require_coverage` on the step: `none` (default), `all` changed files, or a
    list of paths. A review that does not meet it retries up to the step's
    attempt limit, then escalates (`attempts_exhausted`).
  - Or a route condition on the `coverage` output.
  - The library's standard code-review template sets `require_coverage: all`;
    specialised reviews may narrow or omit it.
- **Reviews follow the branch tree.** At every level (ticket, epic, project, or
  whatever levels the workflow uses) the work is reviewed against its parent
  branch: a ticket against its epic branch, an epic against its project branch,
  a project against `main`. A project-level review exists when a workflow has a
  project level. Each level looks for problems that exist only once its
  children are combined, and its prior findings include every child's findings
  ledger (DR11).
  - A ticket review is given the ticket and its parent's contract. Given the
    epic contract, a ticket review catches a ticket that departs from it
    (`docs/17` section 6.6, the review pilot).
  - In Spike 3 the epic review caught a seeded cross-ticket defect 20 times out
    of 20, which both ticket reviews, given only ticket text, missed 20 out of
    20 (`docs/18` section 3.1).
- **`prior_findings` is required.** The findings ledger, not any single pass,
  decides what is open (section 9.4).
- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet, **never on line numbers** (`docs/01` section 7.2).
- A code host's pull request review, or an AI review posted on a PR, plugs in as
  a review provider composed with the code host, not as a code host method.

---

## 9. Findings steps

These steps act on the findings ledger: the engine's record of every finding and
its status (section 9.4).

### 9.1 `findings.triage`

| Element | Detail |
|---|---|
| Form | Activity, or a human gate |
| Inputs | `finding_refs`, `mode` (`agent`, `human`) |
| Requires | Review provider for agent mode |
| Outcomes | `completed`, `blocked`, `attempts_exhausted` |
| Idempotency | By fingerprint. A finding is **triaged once** |
| Limits | Attempts 2 |
| Audit | Each triage, with actor and reason |

Contract details:

- Each finding is fixed, declined with a recorded reason, or escalated.
- **Declined is sticky.** A declined finding stays declined until the code its
  fingerprint points at changes. It never restarts the review loop.
- **Fixed is sticky** (position L2, section 9.4). A finding raised again at a
  fixed location whose code is unchanged is recorded as a re-raise and is not
  triaged.
- A finding that reappears after being fixed twice **escalates** instead of
  looping.

### 9.2 `findings.post_declines`

Posts decline reasons on the PR.

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `pr_ref`, `finding_refs` |
| Requires | Code host `ReviewComments` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, PR, fingerprint |
| Limits | 30s, attempts 3 |
| Audit | Decline posted |

### 9.3 `findings.evaluate_readiness`

Evaluates the merge bar (decision D8).

| Element | Detail |
|---|---|
| Form | Local, pure |
| Inputs | The findings summary from workflow state |
| Outcomes | `ready`, `findings_open` |
| Idempotency | Pure |
| Audit | Readiness evaluated |

Contract detail: the bar is **no unresolved blocking finding, and every finding
fixed or declined with a recorded reason**. Not "zero findings". A demonstrated
bug finding (section 8.2) is blocking.

### 9.4 The findings ledger

The version 1 position, accepted 2026-10-30 (`docs/17` section 6.5). It is how the
engine handles review verdicts that differ between passes on the same input
(DR4; Spike 2 measured this, `docs/16` section 2, check R4).

| # | Position | Where enforced |
|---|---|---|
| L1 | **The ledger is the source of truth; a review pass is evidence.** A pass adds to the ledger; it does not replace it | `review.run`, `findings.evaluate_readiness` |
| L2 | **"Fixed" is sticky, enforced by the engine.** When `fix.verify` passes, the ledger records the location (path and enclosing symbol) and that symbol's code hash. A later finding there is recorded as a re-raise, not acted on, while the hash is unchanged. Cost: a genuinely new problem in an unchanged fixed symbol waits until that code changes | `fix.verify`, `findings.triage` |
| L3 | **New findings on unchanged input are kept.** No agreement voting between passes in version 1: noise costs a triage, but a suppressed defect costs a wrong outcome | `review.run` |
| L4 | **At epic scope (and at any level with children), prior findings include the children's ledgers** (DR11), so a parent's findings are matched against its children's findings by L2's rule | `run.child`, `review.run` |

---

## 10. Fix verification

### 10.1 `fix.verify`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `finding_ref`, `diff_ref` |
| Requires | Review provider (whichever reviewer the workflow names), sandbox |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, fingerprint, commit |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the original finding, the full diff. Out: verification result |
| Audit | Verification result |

Contract details:

- **A fix must add a regression test**, or the step fails. The one exception is a
  finding whose triage record says "regression test not applicable", with a
  reason. Some findings cannot sensibly have one: log message content, OpenAPI
  metadata, documentation. The verification review judges whether the reason is
  acceptable; an unjustified reason fails the step. Spike 2 checked all three
  cases (`docs/16` section 2, check K4).
- **The requirement is a step invariant, not a template switch.** The escape is
  carried in the finding's triage record, not in the plan, so a template cannot
  relax this verification floor.
- The fix is checked **against the original finding on the full diff**, not only
  the changed lines. The prototype's re-review reported no issues after fixes
  that had introduced new ones.
- **On success, the step makes "fixed" sticky**: it records the location and code
  hash in the ledger (position L2, section 9.4).

---

## 11. Waiting and artifact steps

### 11.1 `wait.for_state`

| Element | Detail |
|---|---|
| Form | Durable wait with polling, or inbound event |
| Inputs | `target` (PR, check, tracker issue), `state`, `timeout` |
| Requires | The provider for the target |
| Outcomes | `completed`, `blocked` (timeout), `attempts_exhausted` |
| Idempotency | Safe to repeat |
| Limits | Step-defined timeout; heartbeat via the poll |

Contract detail: the general form of `ci.wait_checks`. It never blocks inside a
provider call. It picks one of the two wait forms in `docs/05` section 1.2.1 from
`target`:

| Target | Form | Why |
|---|---|---|
| Something **actively progressing**: a CI run, a check, a merge queue entry | Poll inside one long-running activity, heartbeating | The wait is minutes to an hour and progress is continuous. One activity per step (risk R20, polling inflating history) |
| Something **idle until a human acts**: a tracker transition, a code host review approval | Durable timer, woken by an inbound event | The wait may be days. Holding a worker slot and a heartbeat for that is waste, and an idle poll loop looks like a stuck step |

Either way the event only wakes the step; the step reads the remote state to
confirm (`docs/04` section 1.3.1, "events wake, reads confirm"). Spike 2 dropped
an inbound event on purpose, and polling resolved the gate in 2 polls (`docs/16`
section 2, check K5).

### 11.2 `artifact.bundle`, `artifact.validate`, `artifact.import`, `artifact.publish`

| Step | Form | Contract |
|---|---|---|
| `artifact.bundle` | Activity | Collects artifacts into one bundle. Out: bundle reference |
| `artifact.validate` | Local or activity | Validates an artifact against its schema. Outcomes `completed`, `failed` |
| `artifact.import` | Activity | Imports an external artifact into the store. Idempotent by content hash |
| `artifact.publish` | Activity | Publishes artifacts to a repository for human review. **Git is not a store** (`docs/01` section 2.1); this is how artifacts reach humans in Git |

All are idempotent by content hash or by a key from run, step and target. The
activity forms also return `attempts_exhausted`.

### 11.3 `notify.send`

Tells humans things that are not decisions: "epic delivered", "spec ready for
review".

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | any |
| Inputs | `channel`, `message_ref`, `audience` |
| Requires | Notifications `Channels[channel]` |
| Outcomes | `completed`, `failed`, `attempts_exhausted` |
| Idempotency | Key from run, step, channel, message hash |
| Limits | 30s, attempts 3 |
| Error overrides | A notification failure is **infrastructure** and retries. It never fails the run |
| Audit | Notification sent |

Contract detail: this is `INotificationsProvider.SendAsync` as a step. It differs
from `human.gate`, which waits for an answer.

---

## 12. Delivery steps

### 12.1 `delivery.deploy_order`

| Element | Detail |
|---|---|
| Form | Local, pure |
| Scope | delivery |
| Inputs | `repositories`, `order` |
| Outcomes | `completed` |
| Idempotency | Pure |

Contract detail: the order comes from plan data (`docs/01` section 7.3).

### 12.2 `delivery.deploy`

| Element | Detail |
|---|---|
| Form | Child workflow, one per repository (decision DP3) |
| Scope | delivery |
| Inputs | `repository`, `environment`, `version` |
| Requires | Code host `PullRequest`. `CheckStatus` or CI for the optional deployment-status wait |
| Outcomes | `completed`, `failed`, `blocked`, `attempts_exhausted` |
| Idempotency | By repository and run. The delivery ledger records what already happened |
| Limits | Step-defined |
| Audit | Deploy triggered, outcome, ledger entry |

Contract details:

- **Where delivery happens is the workflow's choice**: once at the end
  (typically epics into a project branch, the project into `main`) or several
  times along the way. The engine supports each and provides the guardrails.
- **In version 1, delivery is merging the repository's delivery PR** (through
  `scm.merge`, section 6.5; into the default branch a human merges unless the
  default-branch setting allows the engine), then optionally `wait.for_state` on a deployment
  status the code host or CI reports. Many organisations deploy on merge.
  **There is no deployment provider in version 1**; add one only if workflows need
  to trigger deploys directly.
- **No compensating action.** The engine triggers and gates deploys; it does not
  perform or roll them back (assumption A7; `docs/01` section 7.3).
- The workflow reads the delivery ledger through an activity, never directly.

### 12.3 `delivery.gate`

| Element | Detail |
|---|---|
| Form | Human gate |
| Scope | delivery |
| Inputs | `gate_id`, `status` |
| Outcomes | `approved`, `rejected` |
| Idempotency | By `gate_id` |
| Limits | No timeout |

Contract detail: on failure the run stops here with status `partial` and three
choices, decided by a human: **retry**, **fix forward**, or **revert** (the engine
opens revert PRs, section 6.6).

### 12.4 `delivery.report`

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | delivery |
| Inputs | `ledger_ref` |
| Outcomes | `completed`, `attempts_exhausted` |
| Artifacts | Out: the report |

---

## 13. Tracker steps

All require the tracker provider. Agents have **no** tracker identity; these steps
are the only path (`docs/01` section 4.6).

| Step | Form | Inputs | Outcomes | Idempotency |
|---|---|---|---|---|
| `tracker.create` | Activity | `spec` | `completed`, `failed` | Key from run, step, marker |
| `tracker.update` | Activity | `issue_ref`, `spec` | `completed`, `failed` | Last write wins |
| `tracker.transition` | Activity | `issue_ref`, `to_state` | `completed`, `blocked` | Transitioning to the current state succeeds |
| `tracker.link` | Activity | `from`, `to`, `link_type` | `completed`, `failed` | An existing link of the same type succeeds |
| `tracker.comment` | Activity | `issue_ref`, `body_ref` | `completed`, `failed` | Key from run, step, issue, body hash |

All: 30s, attempts 3, and all also return `attempts_exhausted`. Field mapping is
provider configuration, not code.

---

## 14. Coverage against the brief

The areas `brief.md` section 8 says the catalogue must cover:

| Brief area | Steps |
|---|---|
| Agents | `agent.run` (with `write_access`; roles are workflow content) |
| Humans | `human.gate`, `human.session`, `policy.auto_approve` |
| Composition | `run.child` (a level starts the runs of the level below), `parallel`; `run.wait` reserved |
| Re-planning | `plan.replan`, `plan.validate` |
| Steering | Not a step. A control-plane capability (`docs/01` section 3.4) |
| Source control | `scm.create_branch`, `scm.merge_base_forward`, `scm.open_pr`, `scm.update_pr`, `scm.merge`, `scm.revert` |
| CI | `ci.trigger`, `ci.wait_checks`, `ci.read_results` |
| Testing | `test.run_tiers`, `test.run_composition` |
| Static analysis | `analysis.run` |
| Review | `review.run` |
| Findings ledger | `findings.triage`, `findings.post_declines`, `findings.evaluate_readiness` |
| Fix verification | `fix.verify` |
| Waiting | `wait.for_state` |
| Artifacts | `artifact.bundle`, `artifact.validate`, `artifact.import`, `artifact.publish` |
| Multi-repository delivery | `delivery.deploy_order`, `delivery.deploy`, `delivery.gate`, `delivery.report` |
| Tracker | `tracker.create`, `tracker.update`, `tracker.transition`, `tracker.link`, `tracker.comment` |
| Notifications | `notify.send` |

---

## 15. Open questions

From the code review design (`docs/19`), not yet decided:

- Which review angles run at ticket level and which only at higher levels.
- Whether a reviewer may run the full test suite, or only targeted tests.
- How a reasoned (undemonstrated) finding is triaged: blocking, advisory, or
  routed to a human.
