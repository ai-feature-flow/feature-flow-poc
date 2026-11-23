# Console B1 results: deciding and navigating

B1 of the console's second slice, built 2026-11-24 on `stuart-parkinson_xero/feature-flow-console-b1` from a build plan,
a foundations commit that fixed every contract, record and store first, six packages in parallel (API reads, API
decisions, engine records, code host and pull request state, repositories with the session and Slack link, the console
shell), three console packages on the shell (workspace, decision popups and steering, Needs me and Runs), an
independent review and a fix pass. **Every B1 item of the console design (its section 8) is built**; Steer,
Edit step's prompt and the merge popup's trace are built partly, each shown with its reason (below). No workflow command or carried state was added, so every
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
| The tree | Child runs nested, markers (running, needs you, breakpoints from the margin, each with a reason), `used/budget`, roll-ups, placeholders, sticky rows (the innermost four enclosing the first row shown; the tree scrolls by whole rows), pills cycling in tree order, Follow current; ↗ opens a child run as the root of its own tree (LOG 534, 541) |
| Root page | Header with Run actions (Abort, History), facts, Needs you in tree order, Progress of the runs under it (LOG 534) |
| Step detail | Path, Needs you card with why first, Running now, the four boxes, Overview and Artifacts tabs (LOG 534) |
| Steer and Edit step | In a step's detail only, each with its consequence and a reason: pause before, skip, grant attempts; Edit step amends parameters or limits for the next attempt, starting from their current values and showing them before and after, or changes the role (LOG 535, 538) |
| Viewer | A preview tab the next artifact replaces, pinned tabs, full size by double-click or ⤢, YAML and JSON as tables, source and previous revision (LOG 534) |
| Decision popups | Intervention, triage, spec approval, merge, patch approval with the proposed plan, breakpoint, question, planning session card; answers and comment at the top; a click outside or Escape closes (LOG 535) |
| Routes | `#/needs`, `#/runs/{tree}/steps/…`, `…/waits/…`, `…/artifacts/…`, and `#/own/{run}…` for a run as its own tree; slice 1's links still open (LOG 533, 541) |
| Start a run | Repository picker with inline registration, replacing the comma-separated field (LOG 536) |

### Not built, or built partly

| What | Why | Shown as |
|---|---|---|
| Steer "go to a step" and "re-plan" outside an intervention | No override exists for them; engine work with E4 (B2) | Disabled: "only at an intervention (B2)" |
| Merge popup's criterion → test → result for a ticket | Only `std:fragments/trace` writes a `trace_report`, in project and epic templates | The run's (or nearest ancestor's) trace; otherwise the criteria, marked "no trace in this run" |
| "Open review report"; Run actions beyond abort (restart, re-run, policy in force) | A14 and E-items, B2 | Disabled with the reason |
| Edit step's prompt | An `amend` sets `with`, `limits`, `outputs`, `consumes` and `produces` only; an agent step's prompt comes from its role and task, so amending it is engine work (B2). Changing the role is the only route to another prompt today | Disabled: "set by the step's role and task; an amend cannot change it (needs engine work, B2)" (LOG 541) |
| Activity, Findings and Events tabs on a step | B2 | Disabled "in B2" |
| A local Git repository in A16 | E5 | `POST /repositories { path }` refused with the reason |
| Settings page | Not in B1's console list | Greyed; registration is inline in Start a run |
| Records of runs started before B1 | Nothing is back-filled | Run id for the title, "–" for attempts, level and start |

## Evidence

The sub-orchestrator's runs at the merged head 34d2b53 (later commits change `docs/` only). The fix pass's runs at its
head 163472d agree: default suite green twice, Integration green. The builders' own figures are in their reports.

| Check | Result |
|---|---|
| Default suite (`Category!=Integration&Category!=Live&Category!=Cluster`), head 34d2b53 | 26 assemblies, 0 failed (Api 240, Engine 308, Interpreter 229) |
| `Console.Tests` (Playwright) | 87/87 in the full run; once earlier, under load from a parallel build, a live re-read test timed out (made robust by the fix pass, M1) |
| `Replay.Tests` | 47/47, at every builder's head and the merged head |
| Integration category (Temporal dev server, own port 7823) | Integration 135/136, Api 2, Postgres 37, S3 12, Docker 17. The one failure, `ReminderTests.An_open_decision_is_reminded_once_per_interval_until_it_is_answered` (an `UnhandledCommand` in the history, a signal arriving as a workflow task completed), came while a second build and test run loaded the machine; it passed 3/3 alone and in the full run at the wave-1 head. Load-sensitive, not caused by a B1 change as far as seen |
| `OpenApiTests` (committed `openapi.json` current) | 4/4 at the merged head (the fix pass regenerated it for LOG 538) |
| Break-and-see-fail | About 120 checks by the builders and 20 by the fix pass, each listed in their reports; the sub-orchestrator's three: A10's check disabled → `AnswerCheckTests` fails; A11's sent-back entries dropped → two `ConsoleRecordsTests` fail; the tree's order reversed → `TreeTests` (pills) and two `WorkspaceTests` fail |

## Review

The independent review at 7f9b3b7 found B1 built as designed and nothing blocking: one major finding (Edit step could
replace a step's whole `with` or `limits` with what was typed, dropping other keys), minor gaps against the design,
two count differences between Runs and the tree, and small visual differences from the mock-ups. The fix pass fixed
every finding, each with a test seen to fail without it (LOG 538 to 541): Edit step starts from the current values; Runs
and the tree count the same steps; an answer's role check comes before its artifacts are stored; ↗ opens a child as its
own tree; sticky rows are the innermost enclosing ones; the margin's breakpoint asks a reason; the shared test scenario
is realistic (one wait per step, run #198's exhausted intervention). One finding is narrowed rather than built: the
prompt in Edit step (above, not built). Accepted for B1 without a change (review R13): any signed-in person may register
a repository, and so learn through the GitHub App which private repositories it can see; the registered list is shown
to everyone. Revisit with shared installations (LOG 532).

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
   for you; hover in the left margin of a pending step for the red breakpoint dot (click it, give a reason, Pause
   before; click again to clear, with a reason). `used/budget` sits at the right of a step that has run, red when spent.
   Scroll a deep tree: the rows enclosing the top row stay pinned, and scrolling stops on whole rows. ↗ on a child run
   opens it as the root of its own tree. The pills at the top (Needs you,
   Running, Problems) step through their rows in tree order and read "(k/n)". Follow current moves the selection as
   steps change while the run runs. For a project or epic, the child runs sit under the step that started them, and
   tickets not yet planned are grey placeholders.
6. **A step.** Click a step: its path, its four boxes (status, attempts, last outcome, agent or action), the Overview
   tab (executions, outputs, routes, prompt) and Artifacts. Click an artifact: it opens in the viewer's preview tab
   (italic), the next one replaces it, 📌 keeps it; double-click or ⤢ opens it full size. A YAML or JSON artifact shows
   as tables, with its source a click away. Old runs show "–" for attempts.
7. **Steer.** On a pending step, Steer ▾ → Pause before: the consequence is shown and a reason is required; apply and
   see the breakpoint dot. Go to a step and Re-plan are disabled "only at an intervention (B2)". Edit step → Limits
   starts from the step's current limits; change one and the Now/After pair shows the others kept. Its Prompt option is
   disabled (B2).
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

## Tweaks after first use

Four fixes the product owner asked for after first using B1. Console, its tests and docs only: no API or engine change.
Screenshots (dark, 1440 × 900) from `PopupTests.Screenshots_of_the_tweaks_after_first_use`, run explicitly with
`FF_SCREENS` set.

1. **A popup fills the window.** Every popup has ⤢ next to its ×: pressed, it fills the browser window and follows its
   size, its body scrolling inside; pressed again, it goes back to its own size. A button, so the keyboard reaches it,
   titled for what it will do. Every popup rather than only an artifact's: decisions hold large tables too.
2. **Folding the branch that holds the selection** selects the branch, as a click on it does (the middle pane and the
   address follow), then folds it. It also turns Follow current off, since following would select the next step
   to change inside and open the branch again. Other manual navigation (a click, a pill) leaves Follow current on, as
   before: the brief assumed it turned it off; it never did.
3. **A pop-out icon after every link to an artifact** opens it full size in a popup straight away; the link still opens
   it in the viewer. One component (`app/viewer.tsx`, `ArtifactLink`) used everywhere one is linked: a step's
   executions, inputs, outputs, prompt and Artifacts tab; a decision's consumed artifacts (Needs me and the workspace);
   the viewer's Previous revision. The run's root page links none. In a decision popup the link opened a popup already
   (popups sit outside the workspace's viewer), so there the icon adds a second way, kept for consistency.
4. **Markdown is formatted** (LOG 542) where the API reads it as Markdown (a declared type whose format is `markdown`,
   or front matter), where the artifact's name ends `.md` or `.markdown`, and for prompts. Artifacts carry no content
   type, so there is none to go by. Built by a small reader of our own (`ui/markdownTree.ts`, no dependency) into a
   tree that `ui/Markdown.tsx` turns into elements: raw HTML is text, and links are links only for `http`, `https` and
   `mailto`, opened in a new tab with `rel="noopener noreferrer"`. The source view is unchanged, and **Download** now
   shows in both views, transcripts included (it reads the bytes when pressed). YAML and JSON stay tables and lists;
   transcripts (JSON lines) and other text stay text.

Evidence: Playwright tests for each, which fail on the console before the change (`PopupTests`, `TreeTests`,
`ViewerTests`, `DecisionTests`, `ArtifactTests`), the last including a real run's prompt (run-947be4435f5c's
`plan.plan`, execution 2, read from the live instance as `tests/FeatureFlow.Console.Tests/data/plan-prompt.md`) and a
hostile document; the reader's unit tests (`src/FeatureFlow.Console/tests/markdownTree.test.ts`, run by `npm test`,
which `npm run build` now runs), the hostile document among them.

## Open questions

1. A code host outage at registration answers 400; should it be a new 503 (LOG 532)?
2. Revisit open registration (the probe and the unfiltered list) with shared installations (LOG 532).
3. `can_mark_fixed` approximates "committed since raised" from recorded revisions (LOG 529); acceptable until the API
   reads the work branch?
4. Edit step's prompt: build prompt amendment in B2 (a step-level prompt override in the engine), or record that the
   role is the route to another prompt (LOG 541)?
5. A status wait (`stalled`, `stuck`) on a container step is no longer a current step; should a stalled container show
   somewhere, on its first branch or the run's page (LOG 539)?
6. The dark theme's needs-you row is now Nord yellow with dark text, close to the light theme's highlight; worth the
   product owner's eye, as is whether whole-row scroll snapping in the tree feels stiff on a trackpad.
