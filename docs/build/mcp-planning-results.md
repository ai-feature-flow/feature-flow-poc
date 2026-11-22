# Agent planning results: `inspect_plan` and `propose_patch`

Built 2026-11-22 on `stuart-parkinson_xero/feature-flow-mcp-planning` from a design note, an interfaces commit and
three parallel builders: (a) the agent side (the sandbox client's tools, the agent endpoint, the run-context
paragraph), (b) the core (a pure patch check for previews, the query handlers, `planning.templates`, a child's
`review` at its parent, the approval prompt) and (c) std, the console and the documents. It is step 2 of LOG 474's
options note: an agent step with `planning` proposes a patch to its own run, or its parent's, and a person approves
it where the rules say. No live run was made; the product owner's live steps are below.

Evidence from the sub-orchestrator's own run at `d1f0697` (the merged branch), below.

## What was built

| Item | Change |
|---|---|
| Tools | `inspect_plan` and `propose_patch`, offered only to a step with `planning` (`FF_PLANNING`); `GET /agent/v1/plan`, `POST /agent/v1/patches`; the staged patch kept in the `patch_staged` agent event's detail in the record store and carried by the next accepted `report`, checked again there; a refused proposal leaves the staged one (LOG 519) |
| Core | The patch check split from its commit (`CheckPatch`), so a workflow query previews a patch without changing anything; queries `agent_plan` (`self`, `parent`, and `child` for a child's view of its parent), `patch_preview`, `child_patch_preview`; `planning.templates` enforced on a step's own patches (LOG 522); a child step's `review` applied at its parent (LOG 521); F3 covers a step added inside protected content (LOG 525); a step added or replaced with wider `planning` than its proposer's waits for a person (LOG 526, builder (b)'s R2 fix); the approval prompt written as plain text (LOG 523) |
| Run context | A "Planning authority" paragraph, only when the step has `planning` (`docs/04` section 6.1) |
| Runtimes | Claude Code's allow-list gains `mcp__engine__inspect_plan` and `mcp__engine__propose_patch`; Copilot CLI already allows the `engine` server |
| std | `ticket_delivery.implement` has `planning: { targets: [self], review: required }`; the planners and designer keep theirs (LOG 509) and now get the tools; the fixer gets none (LOG 524) |
| Console | No change: a wait's prompt was already text with `white-space: pre-wrap`; a browser test now covers a patch approval's prompt |
| Tests | std workflow tests `ticket_implementer_patch_approved`, `ticket_implementer_patch_rejected`; `WorkflowTestRunnerTests.An_implementers_patch_to_protected_content_is_held_even_with_review_auto` (a `set` on `verify.tests`, and a step inserted before `deliver.open_pr`, on a `review: auto` copy of `ticket_delivery`); `StdPatchPromptTests` (the core's approval prompt on std's own `ticket_delivery`); `DecisionTests.A_patch_approvals_prompt_is_shown_as_text_line_by_line_and_approve_sends_the_action`; builders (a) and (b)'s tests (their reports) |
| Documents | `docs/04` s3.3 and s6.1 (the one description), `docs/05` s3.1 and s6, `docs/03` s2.2, s9.4, s10 and s10.3, `docs/01` s2.3, s7.1, s7.2 and s10, `docs/00` P3 and P4, `docs/08` s6.1, s6.4 and s6.5, `docs/09` s2 (check 29) and s4; LOG 519 to 526 (unreviewed); LOG 474 marked superseded |

## Evidence

| Check | Result |
|---|---|
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`) | 26 projects, all passed. In the full run two `Console.Tests` cases timed out waiting for the live-events connection (`data-live`) while the machine was loaded; `Console.Tests` alone then passed 49/49 |
| Integration category (Temporal dev server on port 7703) | 197 passed, 0 failed (Integration.Tests 136, Postgres 30, Docker 17, S3 12, API 2), including `AgentContractTests.An_agent_s_proposed_step_waits_for_approval_and_then_runs` (a scripted agent in a real sandbox calls `inspect_plan`, `propose_patch` and `report` through the MCP server; a `patch_approval` opens with the reason in its prompt; approved by the CLI; the inserted step runs) and `ChildRunTests` (a child's proposal reaches the parent's gate). The agent test first failed in review (R1: its scripted patch left the inserted step unreachable, check 9); fixed in the test |
| `Replay.Tests` | 47 passed; the recorded histories include the new `agent_planning_patch` |
| Cluster | Not run: nothing under `deploy/` or the cluster tests changed |
| Break-and-see-fail (sub-orchestrator, each reverted after) | (1) `WiderPlanning` not called: 5 `AgentPlanningTests` cases fail. (2) A step added inside protected content not counted as touching it: 2 cases of `A_step_added_inside_protected_content_waits_for_a_person` fail. (3) A child's `review` not applied at the parent: `A_childs_review_required_is_carried_to_the_parent_whose_approval_opens_at_the_run_child_step` fails. (4) The planning tools listed without `FF_PLANNING`: `The_planning_tools_are_listed_and_served_only_with_planning` fails |
| Break-and-see-fail (builders) | Builder (c): removing `planning` from `implement` fails both std tests (`'migrate' is not a step ... in any revision`; `1 answer(s) were not used`); `verify` unprotected fails the `review: auto` test's `set` case, `deliver` unprotected its insert case; `review: auto` on std's `implement` fails `StdPatchPromptTests` (no wait opens); `white-space: normal` on `.ff-wait-prompt` fails the console test. Builders (a) and (b) did the same for each of their tests (their reports) |

## Judgement calls

The design note's, decided by the sub-orchestrator and recorded as open for the product owner: every std grant keeps
`review: required`; the tool returns at once and the patch rides on the report; no branch is added to a running
`parallel`, so std grants `parent` to no step; the fixer gets no planning. `targets` stays `self | parent`, F3 being
the fence within a run, now covering a step added inside protected content (LOG 519 to 526). `approve_spec` stays
unprotected (open question 1).

## Live-run steps for the product owner

The arrangement is the Copilot live run (`a4-results.md`, "Live run with Copilot CLI"): the same `~/ff-live`,
configuration, secrets, Temporal (port 7833) and three hosts, built from the merged branch. Only the run differs.

1. **Build** the merged branch: `dotnet build FeatureFlow.slnx -c Release`. The sandbox client changed (it serves the
   two tools), so **publish it again**, as in step 1.3 there:
   `dotnet publish src/FeatureFlow.SandboxClient -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o ~/ff-live/sandbox-client`.
   No image change is needed.
2. **Start** Temporal and the three hosts as in steps 3 and 4 there, and open `http://127.0.0.1:8580/console`.
3. **The ticket.** An implementer adds a step only when it meets something the plan did not foresee, and a capable
   model usually just does small extra work itself. Two shapes, the first to check the path, the second to see
   whether it happens unprompted:
   1. *Prompted* (near-deterministic). A fixture issue in `ai-feature-flow/feature-flow-example` that asks for a
      small API change (for example "add `GET /orders/count`") and says: "The README's endpoint table must be updated
      by a separate step after implementation, which the plan does not include; propose that step with
      `propose_patch` rather than editing the README yourself." The planner may still plan the README change; if
      its delivery plan does, edit the issue so the README is not mentioned until implementation, or reject the spec
      once with "leave the README out of the plan".
   2. *Unprompted* (depends on the model). An issue whose obvious plan misses a separate piece of work the
      implementer must not fold into its own, for example a behaviour change that also needs the fixture's seed data
      regenerated by its script, where the issue mentions only the behaviour. If the implementer does it itself or
      reports `blocked`, the run proves nothing about patches and can be stopped.
4. **The run.** Start `std:templates/ticket_delivery` with `ticket_id` the issue, `engine_can_merge` off,
   Repositories `ai-feature-flow/feature-flow-example`. Approve the spec.
5. **What to watch while `implement` runs.** The step's transcript (Artifacts, `transcript:1`) shows the run
   context's "Planning authority" paragraph, then calls to `inspect_plan` and `propose_patch`, the latter answered
   `staged` with `effect: needs_approval` and the reason `implement has planning.review: required`. The step's agent
   events include `patch_staged`. Nothing changes in the plan yet: the plan panel still shows revision 1.
6. **The approval.** When `implement` reports, the timeline shows `patch_pending implement: implement has
   planning.review: required` and "Needs me" lists a `patch_approval` at `implement`. Its form shows the prompt as
   plain text, line by line: `implement proposes a change to the plan (revision 1).`, the agent's `Reason:`,
   `Needs approval:`, `Continues at:` (the new step) and `Changes:` with the operations as YAML. Check the reason
   and the changes, then **approve** (a comment is optional). The CLI does the same:
   `ff run answer <run id> <wait id> approve --reason "<why>"`.
7. **Expected path.** The timeline shows `patch_applied implement: revision 2, by agent`; the plan panel shows
   revision 2 with the new step after `implement`; the new step runs (its own agent session), then `verify` (tests,
   analysis, review, satisfaction) and `deliver` as on the main path. The new step's work is on the same branch, so
   the reviewers judge it with the rest.
8. **Rejection**, optionally, on a second run of the same issue: **reject** with a reason. Expect `patch_rejected
   implement: a person rejected it`, revision 1 unchanged, and the run going on from `implement`'s `implemented` to
   `verify`.
9. **Evidence to record**: the run id; the transcript's tool calls and their answers; the wait's prompt as shown; the
   plan's revisions (`GET /runs/<run id>/plan`); the path after the decision; the premium requests the extra session
   used.

## Open questions

1. Should any std agent patch apply without a person (`review: auto`), for example an `insert` that touches nothing
   protected (design question 1, LOG 524)? If so, should `approve_spec` be protected first: an agent's patch can
   remove it, and a later re-plan then goes to implementation without a spec approval (LOG 524). The other gap the
   probe found, a step inserted inside `deliver`, is closed (LOG 525).
2. Is "the patch takes effect when the step reports, and the tool never waits" right (design question 2, LOG 519)?
3. Should a branch be addable to a running `parallel`, so a child can add a sibling (design question 3, LOG 521)?
4. Should the fixer get planning or a `needs_replan` route (design question 4; LOG 507 question 3, LOG 524)?
