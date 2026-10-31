# Review 08: feedback on the risks and spike plan

Date: 2026-10-27. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/08-risks-and-spikes.md` at commit ee28c60.

## Summary

Good. Treating R11, R18 and R19 as one theme ("an interruption becomes a wrong outcome instead of a continuation") is right, and making measurements the deliverable of Spike 1 is right. The changes below add one risk the start-and-poll decision introduced, fill gaps in failure injection, and answer the open questions.

## Changes requested

| # | Issue | Change |
|---|---|---|
| K1 | **New risk: polling inflates history.** Start-and-poll (review 06, C1) done as one short activity per poll means a 4-hour agent step polled every 30 seconds adds about 480 activities, well over 1,000 history events, for one step. That alone can hit the history limit (R1) | Poll **inside one long-running activity** that loops on `GetCommandStatusAsync` / `GetProgressAsync` and heartbeats. History cost is one activity per step. After a worker crash, the activity retry re-attaches by handle. Add as R20 and measure in Spike 1 |
| K2 | Spike 1 failure injection does not cover Temporal itself being unavailable, which the enterprise profile must tolerate (99.9% SLA) | Add: stop the Temporal server (or block access) for several minutes mid-run. Workers wait and resume; no step fails; the API reports platform status distinctly from run status |
| K3 | Spike 1 failure injection does not cover a network break between the agent worker and the sandbox (only the hook endpoint) | Add: break the worker-to-sandbox connection mid-command. The activity retries and re-attaches; the command keeps running |
| K4 | The fix-verification loop is not tested anywhere. Fixes for review findings introducing new defects was the most visible prototype failure (3 of 11 late findings) | Spike 2: seed a finding, fix it, run `fix.verify` (regression test required, check on the full diff). Also exercise the decline path: decline reason posted on the PR, and the finding is not re-raised on the next review |
| K5 | "Events wake, reads confirm" is not tested | Spike 2: approve a gate from chat (inbound event path). Drop one webhook deliberately and show the polling fallback completes the step |
| K6 | Spike 2 exit criterion "runs to a merged PR without manual intervention" conflicts with floor F1 if the PR targets a default branch | Clarify: target an integration branch, or count the default-branch approval as an expected decision, not an intervention |

## Answers to section 5

| # | Question | Answer |
|---|---|---|
| 1 | Spike 1 plan length | Size it by **history events**, not steps. Use fake providers (the dry-run mechanism, review 07) to generate synthetic plans from about 50 to 400 steps cheaply, and find where continue-as-new triggers. For reference, the prototype's production run reached about 325 steps across an initiative with 16 child ticket runs. Then one shorter run with real providers |
| 2 | Kill the worker or simulate | Both. Simulated for fast iteration; killing the real process (and a real sandbox and agent CLI) is the exit criterion |
| 3 | Spike 2 repository | A purpose-built fixture repository owned by the project: a small .NET API with a Postgres-backed integration tier (Testcontainers) and a GitHub Actions workflow. It doubles as the open-source example and an end-to-end test. A production repository comes later, in the first enterprise adopter's distribution |
| 4 | Real epic for Spike 3 | Synthetic tickets, but designed with a **seeded cross-ticket defect** (for example two tickets that each change one side of the same contract), so the epic-level review has something known to find. LLM behaviour makes an unseeded result inconclusive |
| 5 | Acceptable W0 fallback without hooks | Decided: accepted as below |

## W0 fallback (product decision: accepted)

Decided by the product owner, 2026-10-27. If an agent runtime has no working hooks on Windows, version-1-acceptable control is:
- Write access enforced by the sandbox itself: read-only checkout for roles with `write_access: none`, and no push credential issued.
- Push restricted by the credential broker (per-step token, one repository, ticket branch only where the code host supports branch-scoped rules).
- A post-step diff check: the engine compares the branch before and after, and fails a step that changed what its role may not change.
- Cost recorded from the runtime's own usage output instead of hook events.

Per-tool-call policy (deny before execution) is then unavailable on Windows, and the run records the coarser control.
