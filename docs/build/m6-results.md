# M6: findings, validators and levels

## Findings

Branch `m6-findings` (worker W2), from `m6@c238b4d` (the shared interfaces of PR 9).

### What was built

| Part | Where | What |
|---|---|---|
| The ledger in SQLite | `Providers.Local/Stores.cs` (its own region) | Tables `findings` and `finding_events`; query `findings` over the run tree by a recursive query (LOG 160) |
| `findings.record` | `Engine/Actions/Findings.cs` | From a reviewer's `findings_report` or a findings source by instance name. Fingerprint matching against the run's and its children's ledger, re-raise or new, code change at file granularity, `reopen_on`, outputs `new`, `reraised`, `reopened`, `repeat_fixes`; `unavailable` from a source, or after 30 minutes pending (LOG 162, 163) |
| `findings.triage` | the same | States with actor and reason; fixes counted; declines posted on the open pull request, once per finding. A triage naming an unknown finding is the new outcome `invalid`, nothing applied (LOG 161, 164) |
| Older libraries still load | `Model/Actions.cs`, `Library/PlanLoader.cs` | An action outcome added later escalates when a plan does not route it (`ActionContract.Escalates`, LOG 161) |
| std | `library/std/fragments/fix_loop.yaml` | `invalid` back to the triage's producer: `apply_triage` to `triage`, `record_fixes` to `verify_fix` |
| The `findings` namespace | `Providers.Abstractions/FindingsLedger.cs`, `StepOutcome.Findings`, `RunWorkflow.Apply` | Counts returned with a findings action's outcome and applied (`FindingsChanged`) before it, so its routes read them; carried over a hand-over by the core (LOG 166) |
| The ledger view | `RunWorkflow` (check 32, agent spec), `Engine.Agent/AgentActivities.cs` | `findings_ledger` staged as a `findings_report` with ledger ids and states when the step's sandbox is created (LOG 167) |
| Copilot review source | `Providers.GitHub/CopilotReviewSource.cs`, kind `copilot_review` | Requests Copilot as a reviewer, polls, reads review comments as findings (LOG 165) |
| Stand-in replaced | `EngineActions.Built` | With real source control configured both actions run for real (LOG 121, 146) |

### Checks

| What | Test | Result |
|---|---|---|
| SQLite ledger | `Providers.Local.Tests`: `SqliteFindingsLedgerContract` (5), `SqliteLedgerTests` (1) | Pass |
| Both actions on the in-memory world | `Engine.Tests/FindingsActionTests` (30 cases) | Pass |
| Merge bar reads the counts, also across a hand-over | `Interpreter.Tests/FindingsNamespaceTests` (3) | Pass |
| A pre-M6 `fix_loop` loads, `invalid` escalating; the 4 replay histories replay | `StdLibraryTests`, `Replay.Tests` | Pass |
| std routes `invalid` back to its producer | re-drive `library/std/tests/ticket_triage_invalid.yaml` | Pass |
| std's review and fix loop on the real ledger: a blocking finding, triage and verification from the staged view, the second review clean, the ticket merges | `Integration.Tests/FindingsLoopTests` | Pass |
| Copilot source | `CopilotReviewSourceContract` (4) and `CopilotReviewSourceTests` (8), recorded GitHub | Pass |
| Copilot source, live on the fixture | `CopilotReviewSourceLiveContract` | **Unavailable**: Copilot code review is not enabled on `ai-feature-flow/feature-flow-example`; GitHub answers 201 to the review request without adding Copilot. 3 passed, 2 skipped (they need a review). Every pull request opened was closed; no `ffc/` branch left |

Counts on `m6-findings` before the PR: default suite **799 passed, 0 failed** (746 at `c238b4d`); integration **51 passed**
(Temporal on port 8233); Release build 0 warnings.

### Controls

`tools/check_m6_findings_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker, no model, no GitHub). **All 31 hold.**
Each breakage made its named tests fail and all passed after the revert. Two first failed to hold and were fixed:
"a retry applies the triage again" passed by construction (a plain retry finds the state already set, so the test now
reopens the finding between the attempt and its retry), and the Copilot `blocking` control did not build (an unused
parameter is a warning, so an error).

| Breakage | Tests that failed |
|---|---|
| Ledger: view without descendants; finding not replaced; event key appended again | the contract's tree, last-write and history tests; the child re-raise test |
| Record: no fingerprint matching; `reopen_on` ignored; file change not seen; any commit counts as a change; retry re-applied; bad report accepted; wrong `for_each` branch's report; pending source waited on for ever | the matching re-raise and child tests; the reopen_on theory; the code-change and repeat-fix tests; the other-file test; the retry test; 3 bad-report cases; the branch test; the timeout test |
| Namespace: `repeat_fixes` at three fixes; `open_blocking` counts every open finding; counts not applied by the workflow; dropped from the activity's outcome; lost at the hand-over | repeat-fix; namespace counts; `FindingsLoopTests` (twice); `FindingsNamespaceTests` |
| Ledger view: check 32 refuses `findings_ledger`; view not staged | `FindingsLoopTests` |
| Triage: unknown finding accepted; retry re-applied after the finding moved on; fix not counted; declines never posted; posted despite `post_declines: false`; unstable comment key | the invalid theory; the retry test; triage and repeat-fix tests; the decline test (three times) |
| `invalid`: no engine default; std routes it to `fix` | the old-library test and 3 replay histories; the re-drive test |
| Copilot: older head counted ready; asked again; 201 without Copilot treated as pending; whole hunk as snippet; `blocking` ignored | the matching provider tests |

### Deviations and judgement calls

- LOG 160: the SQLite contract test sends the artifact index to the in-memory store until W1's SQLite index is merged.
- LOG 161: **`findings.triage` gains `invalid`**, and an action outcome added later escalates where unrouted. docs/01
  section 5.4's mechanism (validate produced artifacts at report time, with correction turns) is not built; this is
  the routed alternative the brief asked for. Changes docs/03 section 4.3 and docs/04 section 2.
- LOG 162 to 167: matching and file-granularity code change; the source timeout and interventions for unreadable
  reports; declines posted only with an open pull request; the Copilot mapping; the namespace's definitions and
  transport (`StepOutcome.Findings`, a new optional field in `Steps.cs`); the ledger view's staging.
- Re-drive tests keep scripted `findings.*` values: `Testing.Workflows` may reference only the core (LOG 66, 80).
  The real counting runs in the engine and integration tests.
- Files outside my own that I touched with small additions: `RunWorkflow.cs` (3 places), `EngineActions.cs` (the
  `Built` set, the dispatch, `ActionRun.Instance`, `ActionOptions.ReviewTimeout`), `ActionScope.cs`, `Steps.cs`,
  `StepStartChecks.cs`, `Model/Actions.cs`, `PlanLoader.cs`, `ActionWorld.cs`, `ForgeTicketTests.cs` (std's
  reviewers now write empty reports; `StartAsync` is internal).

### Review of PR 11 (changes-required, 2026-11-03)

The independent review found 6 issues and 8 mutations its tests did not catch. Coordinator: LOG 161 and 164 accepted
for the product owner's list; the rest fixed as follows.

| # | Finding | Fix | Test (control) |
|---|---|---|---|
| F1 | A triager setting `fixed`, `escalated` or `declined` on a demonstrated blocking bug, with no fix, merged | `findings.triage` `states` (std: triager `[open, declined, escalated]`, verifier `[fixed, open]`); engine-counted outputs `escalated`, `declined_blocking`; `fix_loop` routes on them and `findings.open`, not the triager's outputs; an escalation or a declined blocking or demonstrated finding goes to a person (LOG 168) | `FindingsLoopTests.The_triager_alone_cannot_clear_...` (3 cases stop at an intervention, no deliver step; with std's `states` removed the `fixed` case merges), re-drive `ticket_triage_to_person`, engine `states` and count tests |
| F2 | A later reviewer could downgrade an open finding | A re-raise merges: blocking OR, demonstrated wins, higher severity, proof kept | `A_weaker_re_raise_does_not_downgrade_the_finding` |
| F3 | Parallel recorders could apply stale counts | A ledger version (event count, read with the counts) travels with them; the core keeps the newest, across a hand-over. The finding row's lost update is logged, not fixed (LOG 169) | `Counts_read_at_an_older_ledger_version_...`, `Each_findings_action_returns_a_higher_ledger_version` |
| F4 | Two problems of one rule and file without symbol or snippet merged silently | The title joins the fingerprint then; a duplicate in a report is noted | `Two_problems_of_one_rule_in_one_file_...` |
| F5 | LOG 161's default could widen unnoticed | Test that only `findings.triage invalid` escalates by default and an unrouted `analysis.run failed` is still check 7 | `Only_an_outcome_added_later_escalates_...` |
| F6 | Mutations A, C, D, F, H, N, O uncaught | Tests for each, H being the state revision moving at the fix | one test each; controls A to O |

Re-check (approve-with-fixes): R1, a no-op fix with a verifier writing `fixed` merged; now a move to `fixed` is
`invalid` while nothing is committed to the finding's repository since it was raised or reopened
(`The_verifier_alone_cannot_clear_...` and two engine tests; removing the rule merges). R2, `record_fixes`'s `states`
guard is tested (a verifier's decline stops at `verify_fix`) and it routes on the engine's counts (re-drive
`ticket_verifier_to_person`). R3, `states` fails closed: a given list allows exactly its states; check 31 requires a
non-empty subset of the four. All in LOG 168 (my range was used up).

Controls are now 53 (18 added for the review and 4 for the re-check; existing ones retargeted at changed text). Counts after the fixes:
default **815 passed**, integration **54 passed**, Release build 0 warnings.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Validate produced artifacts at report time against their types (docs/01 section 5.4), so a bad `findings_report` or triage goes back to the agent in its session rather than to an intervention or a re-run |
| 2 | std's `ticket_delivery` posts no declines: its fix loop runs before the pull request opens (LOG 164). Post the ledger's declines when `scm.pull_request` opens one |
| 3 | A parent's `findings` counts change only at its own findings actions; a child's change shows at the parent's next one |
| 4 | Copilot review could not be shown reviewing live: enabling it on the fixture is the product owner's choice |
| 5 | M5 follow-up 3 (a re-run reviewer is not told which files it left unread) was not picked up |
| 6 | A fixed rule vocabulary in std's reviewer task: free-text rule ids from context-free reviewers give a new finding per wording, so `repeat_fixes` rarely accrues (review design note) |
| 7 | A person cannot change a finding's state until M7 wires decisions' sessions to `findings.triage`; an escalation is answered by the intervention's recovery actions meanwhile (LOG 168) |
