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
| Core | `from: previous` (the latest artifact of the name from any earlier execution of the producer), re-expansion in `plan.expand`, `expand:` per execution and consumed-keys and branches expectations in workflow tests (LOG 505, 506; the core builder's report). An exhausted step's escalation names the outcome and summary that sent it back (LOG 510) |
| Tests | std workflow tests `ticket_replan_blocked`, `ticket_replan_exhausted`, `ticket_replan_granted`, `ticket_replan_planner_blocked`, `epic_replan_expands_new`, `ticket_replan_recovery` |
| Documents | `docs/08` s6.1 to s6.5; `docs/01` s7.2, `docs/03` s2.2, s7.2 and s9.4, `docs/04` s6.1 and s6.2 (a person's re-plan now works in std); `docs/09` s4.3; LOG 505 to 510 (unreviewed) |

## Evidence

| Check | Result |
|---|---|
| `dotnet test tests/FeatureFlow.Testing.Workflows.Tests` | 64 passed, the six new std tests and the `expect.branches` runner test among them |
| `dotnet test tests/FeatureFlow.Interpreter.Tests` | 192 passed, the two review-fix tests among them |
| `dotnet test tests/FeatureFlow.Library.Tests` | 6 passed |
| `dotnet test tests/FeatureFlow.Validation.Tests` | 96 passed. Four had used the implementer's `blocked` as the example of the template's class default, and failed with the new route; they now assert the route and check the default on a copy without it, and the ticket's readable-form golden file shows `blocked->plan.plan` |
| `dotnet test tests/FeatureFlow.Replay.Tests` | 44 passed, before and after the core merge and after the review fixes: no recorded history changed |
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`) | 26 projects, 1,890 passed, 0 failed |
| Integration category (Temporal dev server on port 7673) | 195 passed, 0 failed (Integration.Tests 134, Postgres 30, Docker 17, S3 12, API 2) |
| The sub-orchestrator's own run at 450f788 (dev server on port 7663) | Default suite: 26 projects, all passed, Replay 44/44. Integration: 195 passed. An earlier run at b5a3e63, with other builds loading the machine, failed two timing-sensitive tests (`StuckRunTests`, `SteeringRunTests` library override); both passed twice alone and in the full run at 450f788 |
| The sub-orchestrator's three break-and-see-fail checks at 450f788 | Removing `blocked: plan`: `ticket_replan_blocked`, `_exhausted`, `_granted`, `_planner_blocked` fail. `from: previous` read from the previous execution only: `ticket_replan_granted`, `_planner_blocked` fail. Re-expansion identity off: `epic_replan_expands_new` fails on `expect.branches`. Each restored; 64 of 64 pass after |

Each new test was seen to fail without its change:

| Test | Change removed | Failure |
|---|---|---|
| `ticket_replan_exhausted` | The `blocked: plan` route | `plan.plan ran 1 time(s), expected 3`; `open escalations are [implement], expected [plan.plan]` |
| `ticket_replan_recovery` | `planning` on `plan_and_validate.plan` | `the answer was refused: re-plan: plan.plan is not an agent step with planning authority` |
| `ticket_replan_blocked` | The `from: previous` consumes on `plan_and_validate.plan` | `plan.plan execution 2 did not consume 'previous/requirements'; it consumed [baseline_test_results, plan_validation]` |
| `epic_replan_expands_new` | `planning` on `plan_children.plan` | `the answer was refused: re-plan: tickets.plan is not an agent step with planning authority`; `tickets.children_2 ran 0 time(s), expected 1` |
| `epic_replan_expands_new` (`expect.branches`, review F2) | Identity matching in `Patches.cs` (`expandedBefore = []`) | `tickets.children's branches are [tickets.children_0, tickets.children_1, tickets.children_2, tickets.children_3, tickets.children_4], expected [tickets.children_0, tickets.children_1, tickets.children_2]` |
| `ticket_replan_granted` (review F1 a) | The earlier-artifacts rule (`from: previous` reading only the previous execution) | `plan.plan execution 4 did not consume 'previous/requirements'; it consumed [baseline_test_results, plan_validation]` |
| `ticket_replan_planner_blocked` (review F1 b) | The same | `plan.plan execution 3 did not consume 'previous/requirements'; it consumed [baseline_test_results, plan_validation]` |
| `ShellToCoreTests.Previous_is_the_latest_earlier_artifact_...` | The carried `EarlierArtifacts` (snapshot writes null) | the resumed core's `previous/notes` was null, expected `sha256:first` |
| `ShellToCoreTests.An_exhausted_step_s_intervention_names_...` (review F4) | The cause passed to the exhaustion's route | `Expected: "attempts exhausted (build reported blocked: the co"··· Actual: "attempts exhausted"` |
| `WorkflowTestRunnerTests.Branches_are_expected_exactly_...` | `expect.branches` in the reader and runner | all four cases fail: `error check 1 (schema conformance): unknown key 'branches' in expect` |

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
- `from: previous` reads a per-step map of the latest artifact of each name from earlier executions, filled when an
  execution is moved aside and carried with the step record; state carried before it reads as the previous
  execution's artifacts, the old rule (review F1; LOG 505).
- An exhausted step's escalation names the step whose route sent it back, which the pending entry now records
  (`Entry.From`, carried); the text is the run context's existing summary entry, cut to 500 bytes (review F4; LOG 510).
- `ticket_replan_granted` grants one attempt at each step the bound stops (`plan.plan`, `plan.validate`, `implement`),
  since each has the same default allowance of 3.

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
   its last; the next return escalates at `plan.plan` with `attempts exhausted`. When the return that spent it was
   the implementer's `blocked`, the text names it: `attempts exhausted (implement reported blocked: <summary>)`
   (LOG 510); a spec rejection's return reads `attempts exhausted`, and the person's reason is in the run context. A
   grant then revises the last plan: `plan.plan`'s inputs include `previous/requirements` (LOG 505).
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
