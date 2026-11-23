# Console B1 results: deciding and navigating

B1 of the console's second slice, built 2026-11-24 on `stuart-parkinson_xero/feature-flow-console-b1` from a build plan,
a foundations commit that fixed every contract, record and store first, six packages in parallel (API reads, API
decisions, engine records, code host and pull request state, repositories with the session and Slack link, the console
shell), three console packages on the shell (workspace, decision popups and steering, Needs me and Runs), an
independent review and a fix pass. **Every B1 item of the console design (its section 8) is built**; four console
items are built partly, each shown with its reason (below). No workflow command or carried state was added, so every
recorded history replays unchanged, and runs started before B1 keep running: they show their run id and "–" where
B1's new records are missing. Nothing was run live; the product owner's live-use steps are below.

## What was built

### API and engine (design section 7)

| Item | Change |
|---|---|
| A1 run summary | A run record carries its level, parent step, tracker issue with the issue's title and link (read once, kept while the issue is the same), and when it first started (kept across continue-as-new, set by the parent for a child). The run list and page show them, with pull request links; a root of an active tree counts its current steps, running steps and waits needing the caller, across the tree. The run page adds pull requests in full, the inputs and the current steps (LOG 528, 530) |
| A2 plan tree | Plan `groups` (fragment uses and branches, a branch labelled by its `for_each` item) and each step's `parent`; per-step counters from a new step-state record (executions, attempts used, allowed with grants, granted), written by the engine as they change; `child_run_id`; `changed_at` (LOG 528, 530) |
| A5 pull request state | A pull request record on every engine read of a run's pull request, written only when something changed: state, size, files (GitHub: first 300), checks of the head, approvals. On the run page, the run list (links) and a merge gate's wait (LOG 531) |
| A6 findings for people | One rule for which findings need a person, in `FindingsLedger`, used by `findings.triage` and the API; each finding with `needs_person`, `decided_by_person`, `raised_by`, `can_mark_fixed`, re-raises and fixes; `GET /runs/{id}/findings/{finding}` with its history. Findings writes raise `run_changed` (LOG 527, 529) |
| A7 structured artifacts | `GET /runs/{id}/artifacts/{ref}/document`: YAML or JSON as data, Markdown as body and front matter, else text; 415 for binary. Consumed artifacts on an open gate carry `type` and `changes` (LOG 529) |
| A8 patch approval | A patch approval's wait record carries the patch and its proposer, reason, needs-approval reasons, resume point and base revision; `GET /runs/{id}/plan?proposed={wait}` is the plan as it would become, steps marked added, changed or removed; 409 for a patch that does not apply (LOG 528, 530). The staged patch on a step is B2 |
| A10 triage check at Send | A person's answer artifacts are checked against the type the step produces for that outcome; a bad one is refused 400 with `errors`, nothing delivered (LOG 529) |
| A11 intervention cause | A wait's `cause`: step, outcome, reason, what it last said, attempts, every time it was sent back and by whom, grants, `likely_to_repeat` (LOG 528, 529) |
| A12 run tree | `GET /runs/{id}/tree` from the topmost ancestor the caller views: runs, plans (or why none), open waits, children. A 1 + 3 + 12 std tree reads in about 130 ms warm (LOG 530) |
| A13 Needs me | `GET /attention` gives each wait's path and the caller's last 10 answers of 7 days (LOG 530, 536) |
| A15 session card | A session's `open_command`; a gate's `session_id` (LOG 529, 532) |
| A16 repositories (GitHub) | `GET` and `POST /repositories`; anyone signed in may register, the product owner's decision of 24 November (LOG 532) |
| E1 pull request titles | With no `title`, `scm.pull_request` is titled after the run's tracker issue ("Add DELETE /widgets/{id} (#200)"), else `"<level>: <run>"`; std's `merge_into_parent` passes none (LOG 531) |
| Slack | A decision's link opens its wait in the workspace (LOG 532) |
| Stores | Tables `step_states`, `pull_requests`, `repositories`, query `recent_answers`, in SQLite, Postgres (schema script 4) and memory, with contract tests (LOG 527) |

### Console (design section 8, B1 row)

| Item | Change |
|---|---|
| Top bar and theme | Needs me with its count, Runs, Start a run, Templates and Settings greyed, the actor, light and Nord dark themes, right-pane toggle; preferences per browser (LOG 533) |
| Needs me (inline questions) | Every open wait for the person, oldest first, filtered by kind, with path, summary and age; questions answered in place, an offered option in one click; Review and answer opens the popup; Go to step; recent answers folded (LOG 536) |
| Runs | One row per run tree; search, Mine/Everyone, Active/Finished/All, child runs on request (LOG 536) |
| Workspace shell | Tree, middle and viewer panes; both side panes resizable, the right collapsible, remembered; a drawer below about 1,000 px (LOG 534) |
| The tree | Child runs nested, markers (running, needs you, breakpoints from the margin), `used/budget`, roll-ups, placeholders, sticky enclosing rows, pills cycling in tree order, Follow current (LOG 534) |
| Root page | Header with Run actions (Abort, History), facts, Needs you in tree order, Progress of the runs under it (LOG 534) |
| Step detail | Path, Needs you card with why first, Running now, the four boxes, Overview and Artifacts tabs (LOG 534) |
| Steer and Edit step | In a step's detail only, each with its consequence and a reason: pause before, skip, grant attempts; Edit step amends parameters or limits for the next attempt, or the role (LOG 535) |
| Viewer | A preview tab the next artifact replaces, pinned tabs, full size by double-click or ⤢, YAML and JSON as tables, source and previous revision (LOG 534) |
| Decision popups | Intervention, triage, spec approval, merge, patch approval with the proposed plan, breakpoint, question, planning session card; answers and comment at the top; a click outside or Escape closes (LOG 535) |
| Routes | `#/needs`, `#/runs/{tree}/steps/…`, `…/waits/…`, `…/artifacts/…`; slice 1's links still open (LOG 533) |
| Start a run | Repository picker with inline registration, replacing the comma-separated field (LOG 536) |

### Not built, or built partly

| What | Why | Shown as |
|---|---|---|
| Steer "go to a step" and "re-plan" outside an intervention | No override exists for them; engine work with E4 (B2) | Disabled: "only at an intervention (B2)" |
| Merge popup's criterion → test → result for a ticket | Only `std:fragments/trace` writes a `trace_report`, in project and epic templates | The run's (or nearest ancestor's) trace; otherwise the criteria, marked "no trace in this run" |
| "Open review report"; Run actions beyond abort (restart, re-run) | A14 and E-items, B2 | Disabled with the reason |
| Activity, Findings and Events tabs on a step | B2 | Disabled "in B2" |
| A local Git repository in A16 | E5 | `POST /repositories { path }` refused with the reason |
| Settings page | Not in B1's console list | Greyed; registration is inline in Start a run |
| Records of runs started before B1 | Nothing is back-filled | Run id for the title, "–" for attempts, level and start |

## Evidence

**To be filled by the sub-orchestrator at the merged head** (the builders' own figures are in their reports; the
review's run at 7f9b3b7 had all 26 assemblies green: Api 239, Console 82, Engine 308, Interpreter 229, Hosting 69,
Replay 47).

| Check | Result |
|---|---|
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`), head `<commit>` | *TBD* |
| `Console.Tests` (Playwright) | *TBD* |
| `Replay.Tests` | *TBD* (47/47 at every builder's head) |
| Integration category (Temporal dev server, own port) | *TBD*; builders ran theirs: Postgres store contracts 37/37, ticket main path and exhausted path, continue-as-new, child runs, forge ticket, merge-action decisions, findings loop |
| `OpenApiTests` (committed `openapi.json` current) | 4/4 at 7f9b3b7 (docs package) |
| Break-and-see-fail | About 120 checks by the builders, each listed in their reports; the fix pass's in its report |

## Review

The independent review at 7f9b3b7 found B1 built as designed and nothing blocking: one major finding (Edit step could
replace a step's whole `with` or `limits` with what was typed, dropping other keys), minor gaps against the design,
two count differences between Runs and the tree, and small visual differences from the mock-ups. The fix pass
addressed them; its report lists each with its test. Accepted without a change: any signed-in person may register a
repository, so the GitHub App's view of private repositories can be probed and the registered list is shown to everyone
(LOG 532).

## Live use for the product owner

The arrangement is the existing live setup in `~/ff-live`: its configuration, secrets, Temporal (port 7833) and three
hosts. B1 adds tables to the record store, created at start, and changes no workflow history, so runs in flight carry
on.

1. **Update.** Once B1 is merged to `main`: `~/ff-live/ff.sh update`. It fetches `origin/main`, builds, publishes the
   sandbox client and restarts the hosts (`--force` rebuilds with no new commit). Check with `~/ff-live/ff.sh status`.
2. **Open** `http://127.0.0.1:8580/console`. The top bar shows Needs me with a count, Runs and Start a run; the theme
   toggle on the right switches light and dark, and is remembered.
3. **Runs.** One row per run tree. Try the search box, Mine/Everyone, Active/Finished/All, and "list child runs too".
   Runs recorded before B1 show their run id as the title and no level badge or start.
4. **A run tree.** Click a run. The workspace opens: the tree on the left, the run's page in the middle (facts, Needs
   you, Progress), the viewer on the right. Drag either splitter; close the right pane from the top bar; reload and
   see both kept. Narrow the window below about 1,000 px: the tree becomes a drawer.
5. **The tree's markers and pills.** A yellow arrow marks a running step; a triangle and a yellow row a step waiting
   for you; hover in the left margin of a pending step for the red breakpoint dot (click sets it, click again clears
   it). `used/budget` sits at the right of a step that has run, red when spent. The pills at the top (Needs you,
   Running, Problems) step through their rows in tree order and read "(k/n)". Follow current moves the selection as
   steps change while the run runs. For a project or epic, the child runs sit under the step that started them, and
   tickets not yet planned are grey placeholders.
6. **A step.** Click a step: its path, its four boxes (status, attempts, last outcome, agent or action), the Overview
   tab (executions, outputs, routes, prompt) and Artifacts. Click an artifact: it opens in the viewer's preview tab
   (italic), the next one replaces it, 📌 keeps it; double-click or ⤢ opens it full size. A YAML or JSON artifact shows
   as tables, with its source a click away. Old runs show "–" for attempts.
7. **Steer.** On a pending step, Steer ▾ → Pause before: the consequence is shown and a reason is required; apply and
   see the breakpoint dot. Go to a step and Re-plan are disabled "only at an intervention (B2)". Edit step shows the
   limits or parameters for the next attempt.
8. **Needs me with an inline question.** When an agent asks a question (`ask_human`), Needs me shows it with a text box
   and its offered options: an option answers at once; or type and Send. The answer appears under "Answered by you
   recently". Other waits: Review and answer opens the popup, Go to step → opens the workspace at the step.
9. **Each popup, as runs reach it.** A ticket run gives, in order: the **spec approval** (Approve / Reject…, what
   changed, requirements and criteria), any **question**, a **triage** if review findings need a person (one card per
   finding needing you; Send stays disabled until each has a state and a reason; "what will be sent" shows the YAML),
   an **intervention** if a step runs out of attempts (why it stopped, what sent it back, Retry / Grant attempts /
   Go to a step / Re-plan / End the run), a **patch approval** if the implementer proposes a step (the plan as it
   would become, rows marked added, changed, removed), and with `engine_can_merge` off the **merge** (the pull
   request's state, files, checks, and the criteria). A breakpoint opens the **breakpoint** popup (Continue, Clear
   and continue); a project run's requirements step opens the **planning session card** (the `ff session fetch`
   command to copy, Submit with a commit). Each popup closes on Escape or a click outside without answering; a
   Slack decision link opens the workspace with that popup open. The new pull request's title is the ticket's title
   and key.
10. **Evidence to record**: anything shown wrongly or missing, with the run id and a screenshot; how the tree reads on
    a project run; whether the pills and Follow current help.

## Open questions

1. A code host outage at registration answers 400; should it be a new 503 (LOG 532)?
2. Revisit open registration (the probe and the unfiltered list) with shared installations (LOG 532).
3. `can_mark_fixed` approximates "committed since raised" from recorded revisions (LOG 529); acceptable until the API
   reads the work branch?
