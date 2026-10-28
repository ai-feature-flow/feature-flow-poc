# Review 13: Spike 1, review 12 applied

Date: 2026-10-28. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/15-spike-1-results.md` and `spikes/01-interpreter/` at commit ba48f45.

## Summary

**Very good. One measurement is invalid; fix it and Spike 1 closes.** A1, A2, A4-A7 and B1-B5 are applied and check out against the code. Carried state is now bounded by plan size (about 500 bytes per plan step, stable across continue-as-new). The threshold is derived from Temporal's limits. A long outage stalls rather than fails. Criterion 8 is measured directly. Nine failure-path histories are committed and replay. The continue-as-new window test loses nothing. Section 12, listing every replaced number, is exactly right.

## A. Must fix

| # | Issue | Change |
|---|---|---|
| A1 | **The new slot measurement cannot be right.** Ten slots cannot run sixteen activities at once, yet the table shows zero schedule-to-start latency at 12 and 16 concurrent long steps, and in one run at 20. The per-run timings show why: in `slots-latency-16.json` runs finished in 0.24 s, 0.96 s and 3.4 s for 20-second steps. The workflows were started staggered (the dev server was returning "busy"), so the steps never overlapped as intended. "Ten slots carries at least 16 concurrent long steps" is withdrawn | Make the overlap real and prove it. Release all steps together (for example each run waits on a signal the harness sends once every run has started). Record actual concurrency on the worker side (the maximum number of activities executing at once) and the configured slot count from the worker log. Sanity check the method first: 20 steps against **2** slots must show schedule-to-start of about one step duration for most activities. If it does not, the measurement method is wrong. Then 20 against 10. Expected: schedule-to-start of about one step duration for roughly 10 activities |
| A2 | Criterion 9 is reported as met on that measurement | Report it as not yet met until A1 is redone. Correct section 6.3 and the section 12 row |

## B. Should fix

| # | Issue | Change |
|---|---|---|
| B1 | The stalled state is count-based (8 failures), so it takes about 8 minutes to appear, because each failure costs a heartbeat timeout plus backoff | Make it **time-based**: stalled after a set time with no successful contact (for example 5 minutes), configurable. That answers open question 3 without shortening heartbeats |
| B2 | 228 record-store files are committed under `results/records` | Fine as evidence for this spike. For later spikes, commit a summary and keep raw output out of the repository, or archive it in one file |

## Answers to section 13

| # | Question | Answer |
|---|---|---|
| 1 | Large plans hit the 2 MB payload ceiling first. Split, or compact further? | Neither for now. Large work already splits naturally into child runs (one ticket, one plan). Add a **plan size limit** as a blocking validation check at load and at patch time, set so carried state stays under 1 MB (about 2,000 steps at 500 bytes each). Record it in `docs/12` |
| 2 | How many repetitions before a pool-sizing rule | Fix validity first (A1). Then three repetitions per level, reporting the spread. The rule itself is arithmetic (slots at least equal to concurrent long steps, plus headroom); the measurement only has to confirm it |
| 3 | 8 minutes to reach stalled | B1: time-based, not count-based |
| 4 | Drop `matches` from the allow-list? | **Yes, drop it for version 1.** It brings a regex engine into workflow code (a backtracking regex is a denial-of-service risk), and no worked example uses it. If it is needed later, reinstate it with .NET's non-backtracking regex and the length caps |

## C. Next: Spike 2

Decided by the product owner, 2026-10-28. Once A1 and A2 are done and Spike 1 is closed, start **Spike 2, one real ticket run** (`docs/08` section 2.2).

| Item | Decision |
|---|---|
| Fixture repository | A **new repository in the `ai-feature-flow` organisation**, as `docs/08` 2.2 describes: a small .NET API, a Postgres-backed integration tier (Testcontainers), a GitHub Actions workflow. It doubles as the open-source example |
| Agent runtimes | **Both GitHub Copilot CLI and Claude Code**, as two named instances of the agent runtime provider (review 07 Q3), selected by role mapping (`docs/09` 11.1) |
| Open models | **Copilot must support open models through bring-your-own-key model routers** (for example an OpenAI-compatible router endpoint). Show at least one role running on an open model through a router. Model and endpoint are configuration (organisation layer and credential references), never template content |
| Other runtimes | The agent runtime stays **pluggable**: nothing in the engine or library may assume Copilot or Claude Code. Record in `docs/15`'s successor which runtime capabilities (`hooks`, `session_resume`, usage reporting) each runtime actually provided |
| Credentials | **Ask the product owner for each credential when it is needed**, one at a time, saying what it is for and the narrowest scope that works. Never commit one; credentials are references to the secrets provider (floor F2). A local secrets file outside the repository is acceptable for the spike |
| Output | `docs/16-spike-2-results.md`, in the same form as `docs/15`: checks met or not, measurements, cost of the run from the record store, bugs found, and the numbers it replaces as it goes |
| Also in scope | The `insert_after` measurement (review 11 C2) and actual `outputs` sizes per kind (review 11 C1) |
