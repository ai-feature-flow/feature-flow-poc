# Console B2 results: understanding

B2 of the console's second slice, built 2026-11-25 on `stuart-parkinson_xero/feature-flow-b2`. The build followed a
plan, then a foundations commit that fixed every contract first (LOG 571). Four packages followed in parallel: engine
and std, API, console understanding, and console steering. Then came the docs, an independent review and a fix pass.
**Every B2 scope item is built.** Two things are refused rather than built: going to a step after the run has ended
(E4), and going to a step inside a container that is running or has not started. No workflow command, signal or update was
added, so every recorded history replays unchanged and runs in flight carry on. Runs started before B2 show "not
recorded" verdicts and no ticket trace (below). Nothing was run live; the product owner's live-use steps are below.

## What was built

### API, engine and std

| Item | Change |
|---|---|
| A3 step detail | `GET /runs/{id}/steps/{step}`: a step's executions with their artifacts, sessions, reports and questions; its waits; its findings and finding steps; its events from every source merged; its staged patch; its pending instructions (`docs/02` section 7, LOG 576) |
| A9 findings frames | `ChangeEffect` is a set of flags. A finding event raises `step_changed` for the step that wrote it as well as `run_changed`. An agent event raises `step_changed` for the run and step of its key `<run>/<step>/<n>` (LOG 577) |
| A14 review report | `GET /runs/{id}/review`: passes, each angle's verdict, what it checked, findings and blocking findings, the run's findings with the pass that raised them and the latest reason, external review, trace, pull requests; a `note` for a run with no review (LOG 578) |
| Policy in force | `GET /runs/{id}/policy`: the plan's decision types with who answers and where the rule comes from, every override key with whether the caller may use it, and attempt limits (LOG 578) |
| E2 reviewer verdict | `findings_report.summary` (`verdict`, `checked`), optional in the type; std's `review_change` task and review skill require it, with or without findings (LOG 574) |
| E3 review summary on the pull request | `scm.pull_request` posts one comment per review pass after the declines, unless `post_review: false`: angles with findings and blocking findings, each verdict and what was checked, the ledger tally. Reviewer text is shown as code spans. Keyed by the reports, so a retry posts once (`docs/04` section 7.2, LOG 574) |
| A8 staged patch | The step detail's `staged_patch` while the agent runs; `GET /runs/{id}/plan?staged={step}`, the plan as a `self` patch would make it (LOG 576) |
| Steer go to and re-plan | Override keys `go_to` and `replan` on a live run: everything in flight is cancelled (steps, child runs, open waits, active containers' branches), then the run enters the target; a reason is required (`docs/05` section 5, LOG 572) |
| Edit step prompt | Override key `instruct` (`instruct_protected` for protected content): a person's text, at most 4,096 bytes, added to the step's next prompt until it next succeeds. The prompt text itself is not edited (`docs/04` section 3.5, LOG 573) |
| Trace for tickets | `ticket_flow` gains an informational `trace` step after `verify`: `requirement_trace` produces a `trace_report`, and a gap does not block (`docs/08` section 6.4, LOG 575) |

### Console

| Item | Change |
|---|---|
| Step page | Reads the step detail; re-reads on `run_changed` for its run and `step_changed` for any of its finding steps |
| Findings tab | The step's findings: id, severity and blocking, title, ledger state, and "Here", the last finding event at the step. "The run's full ledger ⧉" opens the review report (LOG 579) |
| Events tab | Timeline, agent, override, answer and finding events merged, newest first, with a filter by source and a count each; text only |
| Staged patch card | On a running step: target, revision, the agent's reason, operations, the document folded, and "Show the plan as it would become" for a `self` patch; it changes and disappears live (LOG 581) |
| Steer ▾ Go to a step…, Re-plan… | Grouped step picker, clean up, a consequence listing what will be cancelled, a required reason; after Apply the engine's reply names what it cancelled (LOG 581) |
| Edit step → Instructions for the next attempt | Replaces B1's disabled Prompt option: text box, preview of where it goes in the prompt, byte limit, protected note (LOG 581) |
| Review report page | `#/runs/{tree}/review` and `…/review/{run}`: summary, current pass's angles, findings, external review, trace, earlier passes folded, pull requests. Opened from the merge popup's Open review report and the run header's Review report ⧉, in a window of its own beside the pull request (LOG 579) |
| Policy in force | Run actions → Policy in force: a popup of decisions, overrides and attempt limits; a null limit reads "set by the engine" |
| Merge popup trace | A ticket's own `trace_report` is shown; a run with none says it predates the ticket trace or the step has not run (LOG 580) |

### Not built, or refused

| What | Why | Shown as |
|---|---|---|
| Restart a run from a step after it ended; re-run a finished tree (E4) | Out of B2's scope. Go to and re-plan work on a live run only | The core refuses `go_to` and `replan` with "the run has ended"; once a run ends its `overrides_allowed` is empty, so the console disables both. Run actions' "Restart from a step…" and "Re-run" stay disabled |
| Go to a step inside a parallel or chain that is running or has not started | Cancelling the container's other branches and running one would leave its join counting cancelled branches as failed (LOG 572) | Refused by the engine, shown in place: "… is in a branch of <container>, which is running its branches or has not started them; go to <container> to run them all". A step in a decided join runs again, as the recovery go to does |
| Editing a prompt's text | It would let a person drop the reporting instructions (decision 2, LOG 573) | Edit step offers instructions for the next attempt instead |
| A planning flag in the plan view | A contract change. Re-plan therefore offers every agent step | The engine refuses a non-planning target, shown in place |
| A ledger page | Not in the design; the review report lists every finding with its state and reason | The Findings tab's ledger link opens the review report |
| Events tab paging ("earlier ▸ · raw log", mock-up 02) | Not needed at today's sizes | All of a step's events are listed |
| Records of runs started before B2 | Their reviewer reports have no `summary`, and their locked std has no ticket `trace` step | "not recorded" verdicts in the review report; "No verdict recorded." in the pull request comment; no trace in the merge popup |

## Evidence

To be filled by the sub-orchestrator at the merged head.

| Check | Result |
|---|---|
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`), head … | |
| `Console.Tests` (Playwright) | |
| `Replay.Tests` (no history re-recorded) | |
| Integration category (Temporal dev server, own port) | |
| `OpenApiTests` (committed `openapi.json` current; no contract change after foundations) | |
| Break-and-see-fail | The builders' checks are listed in their reports; the sub-orchestrator's own: |

## Review

To be filled after the independent review and fix pass.

## Live use for the product owner

The arrangement is the existing live setup in `~/ff-live`: its configuration, secrets, Temporal (port 7833) and three
hosts. B2 adds no table and changes no workflow history, so runs in flight carry on.

1. **Update.** Once B2 is merged to `main`: `~/ff-live/ff.sh update`, then `~/ff-live/ff.sh status`. Open
   `http://127.0.0.1:8580/console`.
2. **Start a ticket run** on `std:templates/ticket_delivery` from Start a run. A run started before the update keeps
   its locked std: it has no ticket `trace` step and its reviewers write no verdict (step 11).
3. **Findings and Events tabs.** Once the review has run, click a reviewer step in the tree
   (`verify.review.review_<i>.assess`). Findings (n) lists its findings with their ledger state and "Here", the last
   thing done to each at this step. Leave it open while the fix loop triages: states change without a reload. Events
   lists everything that happened to the step, newest first; the filter picks one source (timeline, agent, override,
   answer, finding).
4. **Staged patch.** While `implement` runs, if the implementer proposes a plan change (`propose_patch`, which stages it
   until the agent's report carries it), a "Staged plan change" card appears on the step: what it changes, the
   revision, the reason and the operations. "Show the plan as it would become" shows the plan with steps marked added,
   changed or removed. The card goes when the patch is withdrawn or the step ends; the patch approval follows as in B1.
5. **Steer → Go to a step… and Re-plan….** On a running run, open a step, Steer ▾ → Go to a step…. Pick the target,
   read the consequence (every step running or waiting that will be cancelled, and child runs), give a reason and
   Apply. The popup shows the engine's reply, naming what it cancelled. Re-plan… is the same for a planner step
   (`plan.plan`). Try a target past `verify` from before it: refused, since `verify` is protected.
6. **Edit step → Instructions for the next attempt.** On an agent step, Edit step, choose Instructions for the next
   attempt, type an instruction and Apply. The preview shows where it goes in the prompt. The next execution's
   prompt artifact holds it under "A person's instructions for this step"; it stays until the step next succeeds. On a
   protected step (under `verify` or `deliver`) the form notes that it needs `instruct_protected` (admin).
7. **Review report.** At the merge decision, the merge popup's Open review report opens the report in a window of its
   own: place it beside the pull request. The run header's Review report ⧉ opens the same window. It shows each
   angle's verdict and what it checked, the findings with their states and reasons, external review, and the trace.
   Earlier passes are folded at the end.
8. **The trace.** The merge popup's criteria show the ticket's own trace (criterion → tests → proved, or a gap). A gap
   does not stop the ticket.
9. **Policy in force.** Run actions → Policy in force: who may answer each decision type and where the rule comes from,
   which overrides you may use, and the attempt limits.
10. **The review summary comment.** On the pull request, a comment headed "Feature Flow review": each angle's findings and
    blocking findings, verdict and what it checked, and the ledger tally. A later review pass adds a new comment.
11. **Runs started before B2.** Their review report shows "not recorded" for every verdict, their merge popup no ticket
    trace; if one reaches `deliver` after the update, its pull request comment says "No verdict recorded." per angle.
12. **Evidence to record**: anything shown wrongly or missing, with the run id and a screenshot; whether the review
    report reads well beside the pull request; whether a go to on a live run cancelled what its consequence listed.

## Open questions

The three decisions for the product owner to confirm, each a LOG entry with "Needs a look: yes":

| LOG | Question | Built default |
|---|---|---|
| 572 | Go to and re-plan on a live run cancel everything in flight, including a project's running epics. Is that what "go to any step" should do? | Yes, listed in the consequence before Apply, with a reason required |
| 573 | Is "instructions for the next attempt" what "edit the step's prompt" meant (B1 open question R8)? | Instructions, kept until the step next succeeds; the prompt text is not edited |
| 575 | Should a gap in a ticket's trace block it, as it blocks an epic? | No: shown in the merge popup and the review report |
