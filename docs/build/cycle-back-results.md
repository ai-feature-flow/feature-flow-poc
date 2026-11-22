# Cycle-back results: an implementer's `blocked` goes back to the planner

Built 2026-11-22 on `stuart-parkinson_xero/feature-flow-cycle-back` from a design note and two parallel builders: one
for the core (`from: previous`, re-expansion, the workflow-test format) and one for the standard library (routes,
revising planners, `planning`, tests). It is step 1 of LOG 474's options note: cycling back to planning through the
planner route, revising rather than restarting. Agents still cannot propose a patch (`docs/04` section 6.1); that is
step 2. No live run was made; the product owner's live steps are below.

## What was built

| Item | Change |
|---|---|
| Route | std `ticket_delivery`: the implementer's `blocked` goes to `plan`, without `clean_up`. The bound is the planner step's existing attempt allowance; a spent allowance escalates at the planner. Nothing else is re-routed (LOG 507) |
| Revising planners | `plan_and_validate.plan`, `plan_children.plan` and `epic_delivery.design` consume their own previous artifacts (`from: previous`, optional) and `plan_and_validate.plan` the implementer's claims. `requirements`, `delivery_plan` and `work_list` gain an optional `changes` list; `contract` takes `changes` in its front matter. The four planning tasks gain a "Revising a plan" step; `spec_approval` and `children_approval` ask the person to check `changes` (LOG 508) |
| `planning` | The same three steps have `planning: { targets: [self], review: required }`, so a person's `replan` at them is accepted (LOG 509) |
| Core | `from: previous`, re-expansion in `plan.expand`, `expand:` per execution and a consumed-keys expectation in workflow tests (LOG 505, 506; the core builder's report) |
| Tests | std workflow tests `ticket_replan_blocked`, `ticket_replan_exhausted`, `epic_replan_expands_new`, `ticket_replan_recovery` |
| Documents | `docs/08` s6.1 to s6.5; `docs/01` s7.2, `docs/03` s2.2 and s9.4, `docs/04` s6.1 (a person's re-plan now works in std); LOG 507 to 509 (unreviewed) |

## Evidence

| Check | Result |
|---|---|
| `dotnet test tests/FeatureFlow.Testing.Workflows.Tests` | 58 passed, the four new tests among them |
| `dotnet test tests/FeatureFlow.Library.Tests` | 6 passed |
| `dotnet test tests/FeatureFlow.Validation.Tests` | 96 passed. Four had used the implementer's `blocked` as the example of the template's class default, and failed with the new route; they now assert the route and check the default on a copy without it, and the ticket's readable-form golden file shows `blocked->plan.plan` |
| `dotnet test tests/FeatureFlow.Replay.Tests` | 44 passed, before and after the core merge: no recorded history changed |
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`) | 26 projects, 1,882 passed, 0 failed |
| Integration category (Temporal dev server on port 7653) | 195 passed, 0 failed (Integration.Tests 134, Postgres 30, Docker 17, S3 12, API 2) |

Each new test was seen to fail without its change:

| Test | Change removed | Failure |
|---|---|---|
| `ticket_replan_exhausted` | The `blocked: plan` route | `plan.plan ran 1 time(s), expected 3`; `open escalations are [implement], expected [plan.plan]` |
| `ticket_replan_recovery` | `planning` on `plan_and_validate.plan` | `the answer was refused: re-plan: plan.plan is not an agent step with planning authority` |
| `ticket_replan_blocked` | The `from: previous` consumes on `plan_and_validate.plan` | `plan.plan execution 2 did not consume 'previous/requirements'; it consumed [baseline_test_results, plan_validation]` |
| `epic_replan_expands_new` | `planning` on `plan_children.plan` | `the answer was refused: re-plan: tickets.plan is not an agent step with planning authority`; `tickets.children_2 ran 0 time(s), expected 1` |

## Judgement calls

- No `clean_up` on the `blocked` route: partial work stays on the branch for the next implementer (LOG 507).
- The planner's attempts are the bound, shared with invalid-plan rework and spec rejection (LOG 507).
- `contract` is Markdown, so `changes` is a front-matter key, which its type allows without a schema change (LOG 508).
- The approval prompts point at `changes` rather than render it: a prompt is static text with expressions (LOG 508).
- `project_delivery.define` gets neither `from: previous` nor `planning`: it is a session decision, which already
  seeds its drafts from its previous execution, and not an agent step (LOG 508, 509).
- `epic_replan_expands_new` answers the children's failure with `replan`, so the failed ticket re-runs with the new
  one, and the ticket that passed does not; this also shows re-expansion's rule that only branches that have not
  succeeded run.

## Live-run steps for the product owner

The arrangement is the Copilot live run (`a4-results.md`, "Live run with Copilot CLI"): the same `~/ff-live`,
configuration, secrets, Temporal (port 7833) and three hosts, built from the merged branch. Only the run differs.

1. **Build** the merged branch: `dotnet build FeatureFlow.slnx -c Release`. No image or sandbox-client change is
   needed.
2. **Start** Temporal and the three hosts as in steps 3 and 4 there, and open `http://127.0.0.1:8580/console`.
3. **A person's re-plan** (deterministic). Start `std:templates/ticket_delivery` with `ticket_id` an open fixture
   issue, `engine_can_merge` off, Repositories `ai-feature-flow/feature-flow-example`. On the run page, set a
   breakpoint on `implement` before it starts. Approve the spec. When the run pauses at `implement`, answer
   **Re-plan** at `plan.plan` with a reason that asks for a change, for example "add a criterion for an empty
   list". Expect: `plan.plan` runs a second time; its inputs include `previous/requirements` and
   `previous/delivery_plan`; the new documents keep the first plan's ids and list `changes`; the spec approval
   shows the revision; after approval the implementer runs against it.
4. **An implementer's `blocked`** (depends on the model). Start a second run on a fixture issue whose plan the
   implementer is likely to find unworkable, for example one that asks for behaviour the plan pins to a component
   the repository does not have. Expect: `implement` ends `blocked` with a summary; the run goes back to `plan.plan`
   with no intervention; the planner's context shows the summary; its revision lists `changes`; the spec approval
   opens again. If the implementer does not block, the step proves nothing and can be skipped.
5. **The bound**, optionally: in step 4, reject the spec once more after each revision. The planner's third entry is
   its last; the next return escalates at `plan.plan` with `attempts exhausted`.
6. **Evidence to record**: the run id; `plan.plan`'s executions and their consumed inputs; the two plans' `changes`;
   the intervention or route that sent the plan back; the premium requests the extra planning sessions used.

## Open questions

1. Is the planner's default of 3 attempts, shared with validation rework and spec rejection, the right bound, or
   should std set `limits.attempts` on the planner (LOG 507)?
2. Should the `blocked` route reset the branch with `clean_up` (LOG 507)?
3. Should the fixer and repair agent get a `needs_replan` outcome to the planner? Deferred: another build owns
   `verify_and_review` and `fix_loop` (LOG 507).
4. Should an epic re-plan on its own when a child fails, rather than escalate (LOG 507)?
5. Should the console render `changes` beside the approval (LOG 508)?
