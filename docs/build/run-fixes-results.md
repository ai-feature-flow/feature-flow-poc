# Run-fixes results: the verify/review loop after the Haiku live run

Built 2026-11-22 on `stuart-parkinson_xero/feature-flow-run-fixes` from a diagnosis, three parallel builders, an
independent review and a fix pass. The live run `run-91e59ad2ed43` (Copilot CLI, `claude-haiku-4.5`, ticket #198 of
`ai-feature-flow/feature-flow-example`) looped in std's verify/review loop until aborted; `run-947be4435f5c`
(`claude-sonnet-5.5`) completed cleanly. Every fault found has a fix that does not depend on the model: the loop now
converges or ends with a person, and no path cycles without a bound. LOG 505, 506 and 508 to 513 (unreviewed). No
replay history changed.

## 1. Faults, causes and fixes

| # | Fault (live) | Cause | Fix | LOG |
|---|---|---|---|---|
| 1 | Reviewers raised the commit history (an intermediate non-compiling commit, F-E9EA33EE28) as blocking | `review_change.md` told reviewers to "review every commit" with `git log --stat` | Reviewer, verifier and triager judge the net diff `git diff <baseline>...HEAD` (the baseline is the inputs manifest's merge base, so the range is exact); history is not a finding unless the ticket asks for history work; the triager declines one. `demonstrated` needs a command run and its output | 511 |
| 2 | Declined findings, and the same concern from another pass or angle, came back under new ids; a person was asked again about what they had declined | Identity was only the exact fingerprint (rule and snippet hash); reviewers did not see the ledger, so no re-raise ever matched (0 in three passes) and `reopen_on: code_change` never fired. The person gate required every declined finding again, and a triager's restatement counted as a new decline | Reviewers consume `findings_ledger` and name a repeat with `same_as: <ledger id>`; `findings.record` matches by that id (same repository and file, else refused in the reviewer's session by `document_rules`), so a declined finding stays declined unless its file changed. `findings.triage` counts decisions only, and `complete: true` skips findings a person already decided. Rule: a finding that repeats one in the ledger, any pass, any angle, is the same finding | 505, 506 |
| 3 | `verify_fix` "refused" six times; the run looped until aborted | Not the outcome schema. A failed re-test's results were dropped (list-form `produces` keeps success only), so the second fixer, with nothing open, committed nothing and reported `fixed`; the verifier restated the whole ledger; `record_fixes` (`states: [fixed, open]`) refused restated `declined` entries (and had earlier set three person-declined findings `fixed`); the refusal never reached the re-run agent (one prompt hash in six executions) | Results kept on failure (`retest`, `verify.tests`, `verify.analysis`); `states` limits changes, not restatements, from and to; a fixer's `fixed` with no commit escalates to a person; with nothing open the fixer goes to `retest`, not `verify_fix`; an action's refusal reaches the re-run agent as "Why this step runs again", even under `context: none`, until the step succeeds; load check 37 (blocking) catches a failure route to a step that needs evidence the failing step keeps only on success | 506, 508, 510, 512 |
| 4 | `claims.yaml` committed at the repository root | The repair agent, blind to the failing results (fault 3) and told "write `claims.yaml`" beside "commit everything", wrote it in the checkout and committed it; the engine wrote nothing there | Tasks and the reporting preamble name the output folder for every reported file. In general, the engine fails a step whose own net change (excluding what merging its parent brought) adds a file at a reported artifact's path, read at head through `vcs`; the next attempt is told why | 509, 512 |
| 5 | LOG 503 follow-up: a person's no-change triage re-tested after any earlier fixer | `record_fixes` → `person_triage` was dead (`states: [fixed, open]` makes both counts 0), so the person is reached only before any fixer in the pass | `apply_person_triage` leaving nothing open exits `cleared`; the fallback clause and the dead route are removed. No per-pass engine value is needed (barring a person's Go to) | 513 |

## 2. Evidence

| Check | Result |
|---|---|
| Default suite (`-p:ConsoleBuild=false`, `Category!=Integration&Category!=Live&Category!=Cluster`, console tests included) | All 26 projects pass (Engine 296, Interpreter 186, Engine.Agent 116, Validation 98, Testing.Workflows 52, Replay 44, Api 191, Console 48, …) |
| Integration (`Category=Integration`, own dev server on 7960) | 134 of 134 after one expectation change: `FindingsLoopTests` no longer expects a triage event for a triager restating `open` (LOG 506) |
| Replay | 44 of 44; no history regenerated |
| Break and see fail | Each builder reverted each change and saw its new test fail (builder reports). Rechecked here: base `fix_loop.yaml` fails 3 std workflow tests; base `standard_review.yaml` fails 6 |
| New std workflow tests | `ticket_fix_retest_failed` (with the new `expect.consumed`), `ticket_repair`, `ticket_fix_no_commit`, `ticket_person_clears_after_fix`; `ticket_verifier_to_person` removed with its dead route |
| Unit tests | `FindingsActionTests` (`same_as`, same place, restatement no-op, counts, person not re-asked), interpreter cause tests, artifact-commit guard (own change, parent merge), `MutationTests` check 37 |

Task-wording changes (faults 1, 2 and 4's prose) have no default-suite test: they are prose, and the engine and
routing backstops above are tested instead.

## 3. Left

- Attempts are run-wide, not per pass: a long loop can exhaust a step's attempts across passes (diagnosis s6.3). A
  per-pass allowance is a product owner's decision; with the fixes above, passes should be rarer.
- `same_as` relies on the reviewer naming the repeat; a reviewer that omits it falls back to the fingerprint, and the
  triager (which declines repeats, naming the other id) and the person are the backstop.
- A fixer that only merges its parent has commits, so the zero-commit guard catches "nothing at all", not "nothing of
  its own".
- No live re-run on Haiku has been made (no model quota in this build).
