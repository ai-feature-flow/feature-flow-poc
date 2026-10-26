# Engine step catalogue

Status: first pass, revised for review 06. Date: 2026-10-26. Deliverable 4 of
`brief.md` section 11. Companion to `docs/01-architecture-first-pass.md` section 7,
which holds the grouping, and to `docs/04-provider-interfaces.md` and
`docs/05-provider-contracts.md`, which hold the provider contracts these steps
call.

**Scope.** Every step gets a full contract. Where a step is thin — a direct
wrapper over one provider call — the contract is short and says so. Where a step
carries engine logic, the contract states that logic.

**Review 06 applied.** `agent.review` dropped (one `agent.run`, with
`write_access` as the mechanism); `scm.revert` always gates; the merge lock is per
target branch as a Temporal-native mutex; `test.run_tiers` takes a list of tiers;
`fix.verify` allows "regression test not applicable" with a reason;
`delivery.deploy` is merging the delivery PR, with no deployment provider in
version 1; `human.gate` evaluates the `approval` policy, not `steering`. Added
`notify.send` and `tracker.comment`.

---

## 1. How to read a step contract

| Element | Meaning |
|---|---|
| Kind | The step's identifier in a plan |
| Form | Local (pure, in the workflow), Activity, or Child workflow (section 3.2) |
| Scope | Ticket, epic or delivery. `any` means the step does not care |
| Inputs | What the plan supplies. Artifact references, never inline content |
| Requires | Provider capabilities. Checked at run start (S3) and again if `feature-flow.yaml` changes (section 5.1.2) |
| Outcomes | Named results the plan routes on |
| Idempotency | How an interrupted step detects what it already did |
| Limits | Default timeout, heartbeat, attempt allowance (section 3.7) |
| Error overrides | Where the step reclassifies a provider error, because it knows the intent |
| Artifacts | In and out |
| Audit | Timeline events written |

### 1.1 Outcome vocabulary

Outcomes are the routing vocabulary of the plan. A step returns exactly one.

| Outcome | Meaning |
|---|---|
| `completed` | The step did its work |
| `blocked` | The step cannot proceed; something external must change |
| `failed` | The step did its work and the work was wrong or rejected |
| `attempts_exhausted` | The allowance is spent. Never a silent failure (section 3.7) |
| `replan_requested` | The step produced a plan patch |
| `replan_parent` | A child run asks its parent to re-plan |
| `partial` | Some of the work happened (delivery) |
| `approved` / `rejected` | A human decision |
| `findings_open` | Blocking findings remain |
| `ready` | The merge bar is met (D8) |

### 1.2 Rules every step follows

| Rule | Detail |
|---|---|
| Idempotency key | `hash(runId, stepId, stableInputs)`. Stable inputs exclude anything that changes per attempt |
| Manifest | Each step reads `feature-flow.yaml` from its branch when it starts and records the hash of the resolved manifest (section 5.1.2) |
| No waiting on humans inside an activity | Human waits are durable waits in the workflow, never activity timeouts (R8) |
| Cost | Every step records tokens, model and compute time (section 5.5) |
| Attempts | Counting follows section 3.7. Infrastructure failures are free; resumes are free only with progress |
| Audit | Every step writes a timeline event on entry and on outcome |

---

## 2. Agent steps

### 2.1 `agent.run`

Runs an agent with a role and task inside a sandbox. Under pattern C (section 1.4)
the runtime runs in the sandbox and its hooks call back to the engine.

| Element | Detail |
|---|---|
| Form | Activity (agent worker) |
| Scope | any |
| Inputs | `role`, `task_ref` (artifact), `tools`, `write_access` (`none`, `branch`, `repository`), `limits`, `resume_strategy`, `capabilities` (queue routing, S1) |
| Requires | Sandbox `Os[target]`, agent runtime. `Hooks` optional; `SessionResume` optional |
| Outcomes | `completed`, `blocked`, `failed`, `attempts_exhausted`, `replan_requested` |
| Idempotency | Key from run, step and stable inputs. On retry: `AttachAsync` by key, else create; resume the session by reference |
| Limits | `start_to_close` from the plan (default 4h), heartbeat 30s, attempts 3 |
| Error overrides | An agent that ran out of time is a **work** outcome (`timeout`), not infrastructure. The provider may classify it infrastructure; the step overrides |
| Artifacts | In: task, context. Out: transcript, produced artifacts, session reference |
| Audit | Step start, each resume, outcome, cost |

Contract details:

- **Resume strategy** (section 3.6): WIP commits pushed to the branch, session
  reference in the artifact store, sandbox re-attachment by idempotency key. The
  step records, per attempt, the commit it resumed from and the session reference
  it used.
- **Progress** for the attempt budget comes from `GetProgressAsync`: a new commit,
  or session growth. After 3 consecutive resumes with no progress the next counts
  as a restart (section 3.7).
- **Write access** is enforced twice: by the sandbox (what the role can reach) and
  by the hook endpoint on each tool call (section 1.4). A reviewer role cannot
  change the repository.
- **The result comes from `GetResultAsync`**, not from the session-end hook. The
  hook is a signal (review 05, P3).

### 2.2 Write access is a mechanism, roles are content

`write_access` (`none`, `branch`, `repository`) is enforced by the engine: by the
sandbox, and by the hook endpoint on each tool call (section 1.4). **Roles are
workflow content** (section 1.5), so there is no separate `agent.review` step — a
reviewer is `agent.run` with `write_access: none`, which keeps the floor visible in
the plan (review 06, answer 1).

---

## 3. Human steps

### 3.1 `human.gate`

Waits for a human decision.

| Element | Detail |
|---|---|
| Form | Local policy check, then a durable wait in the workflow |
| Scope | any |
| Inputs | `gate_id`, `prompt`, `required_role`, `min_approvers`, `options` |
| Requires | Notifications (optional, for the reminder) |
| Outcomes | `approved`, `rejected` |
| Idempotency | By `gate_id`. A decision already recorded is not re-asked |
| Limits | **No timeout.** Human waits are unbounded (A3, R8). Reminders are timers, not timeouts |
| Error overrides | None |
| Artifacts | In: what the human is being asked about. Out: the decision record |
| Audit | Gate opened, reminded, decided, with actor and time |

Contract details:

- The decision arrives as a **Temporal Update** carrying the gate id and plan
  revision. The validator rejects a stale or invalid request (section 5.4).
- The API authenticates the human and evaluates the **`approval`** policy, then
  writes the decision record before sending the update (DP8). The `steering`
  policy is for amendments, overrides, extra attempts and abort — not for gate
  decisions (review 06, S6).
- Chat approvals arrive the same way: the chat provider's inbound endpoint
  translates the answer into an API decision call.

### 3.2 `human.session`

An interactive session: prepare a workspace, wait for a submission, validate it.

| Element | Detail |
|---|---|
| Form | Activity to prepare, then a durable wait |
| Scope | any |
| Inputs | `session_id`, `purpose`, `context_refs`, `checks` (which pre-submit checks to offer) |
| Requires | Session surface |
| Outcomes | `completed`, `rejected` |
| Idempotency | By `session_id` |
| Limits | No timeout on the wait |
| Error overrides | None |
| Artifacts | In: context. Out: the submission |
| Audit | Session prepared, checks run, submitted, with the human as the accountable actor |

Contract details:

- The surface **only** prepares the workspace and returns how the human reaches it
  (review 05, P1). Submission and checks are API actions.
- **Pre-submit checks** run the same validators as the gated steps, on demand, so
  no check appears for the first time after submission (section 7.4).
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

Contract detail: a merge into a default branch requires a human. That is a safety
floor, so `policy.auto_approve` cannot approve it (DP6).

---

## 4. Composition steps

### 4.1 `run.child`

Starts a child run on the same interpreter with a scoped plan.

| Element | Detail |
|---|---|
| Form | Child workflow |
| Scope | epic or delivery |
| Inputs | `child_scope`, `plan_ref`, `repositories`, `protected` |
| Requires | Whatever the child plan requires |
| Outcomes | `completed`, `failed`, `replan_parent` |
| Idempotency | By run id and child index. A retry adopts the existing child run |
| Limits | No `start_to_close`; the child has its own |
| Artifacts | In: the child plan. Out: the child's artifacts, and any parent patch |
| Audit | Child started, child outcome |

Contract detail: `replan_parent` carries a plan patch for the parent, applied
through the same `applyPatch` (section 3.4). This is how discovery inside a ticket
changes the epic.

### 4.2 `run.wait`

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
executes (section 3.3.1).

| Element | Detail |
|---|---|
| Form | Interpreter |
| Scope | any |
| Inputs | `branches`, `join` (`all`, `any`, `n`), `max_concurrency` |
| Outcomes | `completed`, `any_failed` |
| Idempotency | Each branch has its own key |
| Limits | `max_concurrency` bounds branches in flight |

Contract detail: a re-plan may add branches to a `parallel` step that has not
started, and may not remove a protected branch.

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
| Outcomes | `replan_requested`, `failed` |
| Idempotency | By run id and plan revision |
| Limits | Attempts 2 |
| Artifacts | In: current plan, context. Out: the patch, and the new plan revision |
| Audit | Patch proposed, validated, applied or rejected, with the diff |

Contract details:

- The patch goes through `applyPatch`, which validates: known step kinds, present
  capabilities, **protected steps not removed or weakened**, routes resolving to
  steps that exist, attempt budgets within policy ceilings (section 3.4).
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

All require the code host provider. All write the hidden idempotency marker (S4)
and adopt existing objects on retry.

### 6.1 `scm.create_branch`

| Element | Detail |
|---|---|
| Form | Activity (orchestration) |
| Scope | any |
| Inputs | `repository`, `branch`, `from_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, repository, branch. On retry, `FindByMarkerAsync` then adopt |
| Limits | 30s, attempts 3 |
| Error overrides | `conflict` when the branch exists at a different SHA is a **work** outcome: reconcile, not retry |
| Audit | Branch created or adopted |

### 6.2 `scm.merge_base_forward`

Merges the integration base forward into a ticket branch (section 6.3).

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `branch`, `into` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `blocked` (conflict) |
| Idempotency | Safe to repeat |
| Limits | 30s, attempts 3 |
| Error overrides | `conflict` routes to an agent step that resolves it, **not to a human first** (section 6.3) |
| Audit | Merge attempted, conflict raised |

### 6.3 `scm.open_pr`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `branch`, `base`, `title`, `body_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed` |
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
| Outcomes | `completed`, `failed` |
| Idempotency | Last write wins; safe to repeat |
| Limits | 30s, attempts 3 |

### 6.5 `scm.merge`

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | any |
| Inputs | `pr_ref`, `method`, `require_human` |
| Requires | Code host `PullRequest`, `MergeMethods[method]`. `MergeQueue` when the target requires it |
| Outcomes | `completed`, `blocked` (checks or protection), `rejected` |
| Idempotency | Key from run, step, PR. A merged PR is already merged |
| Limits | 30s to enqueue; the wait is a durable wait, not an activity timeout |
| Error overrides | `rejected` from branch protection is a **work** outcome: it means a human must approve on the code host, and the engine waits (Q5) |
| Audit | Merge attempted, method, actor, approver |

Contract details:

- **Merge lock, per target branch** (review 06, S3). The lock is keyed by
  repository **plus branch**, so integration branches of different epics in the
  same repository do not conflict. Implemented as a **Temporal-native mutex** — a
  lock workflow per target branch — not an external lock. The step acquires,
  merges, releases.
- **Merge queue.** When the target branch requires a queue, `MergeAsync` is
  refused, so the step uses `EnqueueMergeAsync` and waits by polling
  `GetPullRequestAsync` or by inbound event.
- **Default branch.** A merge into a default branch requires a human. That is a
  safety floor (DP6). The audit record shows the bot as actor and the human as
  approver (section 4.6).

### 6.6 `scm.revert`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `pr_ref` |
| Requires | Code host `PullRequest` |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, PR |
| Limits | 30s, attempts 3 |

Contract detail: serves the revert option of a partial delivery (section 7.3). It
opens revert PRs and **always gates** — it never merges automatically after
approval (review 06, answer 2). Reverts usually target a default branch, which is a
safety floor anyway.

---

## 7. CI and test steps

### 7.1 `ci.trigger`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `tier`, `commit_sha` |
| Requires | CI `Trigger` |
| Outcomes | `completed`, `failed` |
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
| Outcomes | `completed`, `failed`, `blocked` |
| Idempotency | Safe to repeat |
| Limits | Step-defined timeout; heartbeat via the poll |
| Error overrides | A check that fails is a **work** outcome, not infrastructure |
| Audit | Checks waited, final status |

Contract detail: the step polls `GetRunStatusAsync` behind a durable timer. It does
not call a blocking wait (review 05, P1).

### 7.3 `ci.read_results`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `run_ref` |
| Requires | CI `Trigger` |
| Outcomes | `completed`, `partial` |
| Idempotency | Safe to repeat |
| Limits | 30s, attempts 3 |
| Artifacts | Out: structured results, log reference |

### 7.4 `test.run_tiers`

Runs the repository's declared test tiers.

| Element | Detail |
|---|---|
| Form | Activity (agent worker, because it needs a sandbox) |
| Inputs | `repository`, `tiers`, `execution_target` |
| Requires | Sandbox `Os[target]`. `Docker` for Docker-dependent tiers |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, repository, tiers, commit |
| Limits | Step-defined, heartbeat required |
| Error overrides | A test failure is a **work** outcome |
| Artifacts | Out: results, logs |
| Audit | Tiers run, results |

Contract details:

- **One step takes a list of tiers** (review 06, answer 4). Tiers with the same
  execution target share a sandbox; results are reported per tier. A
  Docker-dependent tier delegated to CI runs through the CI provider **inside the
  same step**, so the plan sees one outcome for the whole set.
- **Execution target** comes from `feature-flow.yaml` (section 5.1.1). A
  Docker-dependent tier defaults to **CI delegation** (Q2).
- The step reads `feature-flow.yaml` from its branch when it starts, so a tier
  change mid-run is picked up (section 5.1.2).
- Commands are started and polled (review 06, C1), so a long tier survives a
  worker crash when the sandbox does.

### 7.5 `test.run_composition`

Runs several repositories together as services for integration tests.

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repositories`, `composition_ref`, `tiers` |
| Requires | Sandbox `Docker`, or CI delegation |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, repositories, commit set |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the composition (compose file referenced from `feature-flow.yaml`). Out: results |

---

## 8. Analysis and review steps

### 8.1 `analysis.run`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `repository`, `analyser`, `baseline_ref` |
| Requires | Sandbox, or CI delegation |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, repository, commit |
| Limits | Step-defined, heartbeat required |
| Error overrides | New issues are a **work** outcome |
| Artifacts | Out: results (SARIF or equivalent) |

Contract detail: the analyser comes from `feature-flow.yaml`. The step fails a
change that **adds** issues, not one that has them.

### 8.2 `review.run`

Reviews a diff at any scope with any review provider.

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | ticket, epic or delivery |
| Inputs | `scope`, `diff_ref`, `checks`, `prior_findings` |
| Requires | Review `Scopes[scope]`, `StructuredFindings`. `Fingerprints` strongly preferred |
| Outcomes | `completed`, `findings_open`, `failed` |
| Idempotency | Key from run, step, scope, diff hash |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the diff, prior findings. Out: structured findings |
| Audit | Review run, findings recorded |

Contract details:

- **`prior_findings` is required.** Without it a verdict can change between passes
  on unchanged input (R4).
- Epic scope reviews the **aggregate diff** of the integration branch (DP10).
- Findings are fingerprinted on rule, repository, path, symbol and normalised
  snippet. **Never on line numbers** (section 7.2).
- An AI review on a PR is a review provider composed with the code host, not a
  code host method.

---

## 9. Findings steps

### 9.1 `findings.triage`

| Element | Detail |
|---|---|
| Form | Activity, or a human gate |
| Inputs | `finding_refs`, `mode` (`agent`, `human`) |
| Requires | Review provider for agent mode |
| Outcomes | `completed`, `blocked` |
| Idempotency | By fingerprint. **Triaged once** (D8) |
| Limits | Attempts 2 |
| Audit | Each triage, with actor and reason |

Contract details:

- Each finding is fixed, declined with a recorded reason, or escalated.
- A declined finding stays declined until the code its fingerprint points at
  changes. It never restarts the review loop (section 3.7).
- A finding that reappears after being fixed twice **escalates** instead of
  looping.

### 9.2 `findings.post_declines`

Posts decline reasons on the PR.

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `pr_ref`, `finding_refs` |
| Requires | Code host `ReviewComments` |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, PR, fingerprint |
| Limits | 30s, attempts 3 |
| Audit | Decline posted |

### 9.3 `findings.evaluate_readiness`

Evaluates the merge bar (D8).

| Element | Detail |
|---|---|
| Form | Local, pure |
| Inputs | The findings summary from workflow state |
| Outcomes | `ready`, `findings_open` |
| Idempotency | Pure |
| Audit | Readiness evaluated |

Contract detail: the bar is **no unresolved blocking finding, and every finding
fixed or declined with a recorded reason**. Not "zero findings" (D8).

---

## 10. Fix verification

### 10.1 `fix.verify`

| Element | Detail |
|---|---|
| Form | Activity |
| Inputs | `finding_ref`, `diff_ref`, `regression_test_required` |
| Requires | Review provider, sandbox |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, fingerprint, commit |
| Limits | Step-defined, heartbeat required |
| Artifacts | In: the original finding, the full diff. Out: verification result |
| Audit | Verification result |

Contract details:

- A fix must **add a regression test**. Without one the step fails — **unless the
  plan records "regression test not applicable" with a reason** (review 06, S1).
  Some findings cannot sensibly have one: log message content, OpenAPI metadata,
  documentation. The verification review judges whether the reason is acceptable;
  an unjustified reason fails the step.
- The fix is checked **against the original finding on the full diff**, not only on
  the changed lines. The prototype's re-review reported no issues after fixes that
  had introduced new ones.

---

## 11. Waiting and artifact steps

### 11.1 `wait.for_state`

| Element | Detail |
|---|---|
| Form | Durable wait with polling, or inbound event |
| Inputs | `target` (PR, check, tracker issue), `state`, `timeout` |
| Requires | The provider for the target |
| Outcomes | `completed`, `blocked` (timeout) |
| Idempotency | Safe to repeat |
| Limits | Step-defined timeout; heartbeat via the poll |

Contract detail: this is the general form of `ci.wait_checks`. It polls; it does
not block inside a provider call.

### 11.2 `artifact.bundle`, `artifact.validate`, `artifact.import`, `artifact.publish`

| Step | Form | Contract |
|---|---|---|
| `artifact.bundle` | Activity | Collects artifacts into one bundle. Out: bundle reference |
| `artifact.validate` | Local or activity | Validates an artifact against its schema. Outcomes `completed`, `failed` |
| `artifact.import` | Activity | Imports an external artifact into the store. Idempotent by content hash |
| `artifact.publish` | Activity | Publishes artifacts to a repository for human review. **Git is not a store** (section 2.1); this is how artifacts reach humans in Git |

All are idempotent by content hash or by key from run, step and target.

### 11.3 `notify.send`

Added for review 06 (S4). Workflows need to tell humans things that are not
decisions: "epic delivered", "spec ready for review".

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | any |
| Inputs | `channel`, `message_ref`, `audience` |
| Requires | Notifications `Channels[channel]` |
| Outcomes | `completed`, `failed` |
| Idempotency | Key from run, step, channel, message hash |
| Limits | 30s, attempts 3 |
| Error overrides | A notification failure is **infrastructure** and retries. It never fails the run |
| Audit | Notification sent |

Contract detail: this is `INotificationsProvider.SendAsync` as a step. It is
distinct from `human.gate`, which waits for an answer.

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

Contract detail: order comes from plan data (section 7.3).

### 12.2 `delivery.deploy`

| Element | Detail |
|---|---|
| Form | Child workflow, one per repository (DP3) |
| Scope | delivery |
| Inputs | `repository`, `environment`, `version` |
| Requires | Code host `PullRequest`. `CheckStatus` or CI for the optional deployment-status wait |
| Outcomes | `completed`, `failed`, `blocked` |
| Idempotency | By repository and run. The delivery ledger records what already happened |
| Limits | Step-defined |
| Audit | Deploy triggered, outcome, ledger entry |

Contract details:

- **Version 1: delivery is merging the repository's delivery PR**, then optionally
  `wait.for_state` on a deployment status the code host or CI reports (review 06,
  S2). Many organisations deploy on merge. **There is no deployment provider in
  version 1**; add one later only if workflows need to trigger deploys directly.
- **No compensating action.** A7 holds: the engine triggers and gates deploys; it
  does not perform or roll them back (section 7.3).
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
choices: **retry**, **fix forward**, or **revert** (a human decision; the engine
opens revert PRs).

### 12.4 `delivery.report`

| Element | Detail |
|---|---|
| Form | Activity |
| Scope | delivery |
| Inputs | `ledger_ref` |
| Outcomes | `completed` |
| Artifacts | Out: the report |

---

## 13. Tracker steps

All require the tracker provider. Agents have **no** tracker identity; these steps
are the only path (section 4.6).

| Step | Form | Inputs | Outcomes | Idempotency |
|---|---|---|---|---|
| `tracker.create` | Activity | `spec` | `completed`, `failed` | Key from run, step, marker |
| `tracker.update` | Activity | `issue_ref`, `spec` | `completed`, `failed` | Last write wins |
| `tracker.transition` | Activity | `issue_ref`, `to_state` | `completed`, `blocked` | Idempotent: transitioning to the current state succeeds |
| `tracker.link` | Activity | `from`, `to`, `link_type` | `completed`, `failed` | Idempotent: an existing link of the same type succeeds |
| `tracker.comment` | Activity | `issue_ref`, `body_ref` | `completed`, `failed` | Key from run, step, issue, body hash |

All: 30s, attempts 3. Field mapping is provider configuration, not code
(review 05, P6). `tracker.comment` was added for review 06 (S5), because the
provider has `CommentAsync` but no step used it.

---

## 14. Coverage against the brief

`brief.md` section 8 lists the areas the catalogue must cover.

| Brief area | Steps |
|---|---|
| Agents | `agent.run` (with `write_access`; roles are workflow content) |
| Humans | `human.gate`, `human.session`, `policy.auto_approve` |
| Composition | `run.child`, `run.wait`, `parallel` |
| Re-planning | `plan.replan`, `plan.validate` |
| Steering | Not a step. A control-plane capability (section 3.4) |
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

All six questions from the first pass were answered in review 06 and are applied
above:

| # | Question | Answer |
|---|---|---|
| 1 | `agent.review` as a separate kind | **Dropped.** One `agent.run`; the plan states `write_access: none`. Roles are workflow content; the engine enforces `write_access` (section 2.2) |
| 2 | Should `scm.revert` merge automatically | **No. Always gate.** Reverts usually target a default branch, which is a safety floor anyway (section 6.6) |
| 3 | Merge lock scope | **Per target branch** (repository plus branch), as a Temporal-native mutex. Integration branches of different epics do not conflict (section 6.5) |
| 4 | `test.run_tiers` one step or per tier | **One step taking a list of tiers.** Tiers with the same execution target share a sandbox; results are reported per tier; a CI-delegated tier runs through the CI provider inside the same step (section 7.4) |
| 5 | `wait.for_state` vs `ci.wait_checks` | **Keep both.** `ci.wait_checks` carries specific semantics: late-registering checks, required check names, a grace period before deciding there is no CI (section 7.2) |
| 6 | Default attempt allowances | Accepted as starting values. Tune from the recorded attempt and cost data |

### Still open

None carried forward.
