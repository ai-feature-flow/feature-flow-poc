# Review 15: Spike 2, F1 enforcement and the `main` ruleset

Date: 2026-10-29. From: product owner (via a review assistant). To: architecture.
Reviewed: commits cc9a41f, 8ce38f8 and a636e1a (review 14 A1, A2, A5, A6 in progress).

## Summary

**Good, with one hole in the engine check.** `scm.merge` now reads the pull request's real base branch from the code host, fails closed when the default branch is unknown, and never counts a harness or policy approval for a default branch. The `main` ruleset is real and verified for both tokens, and the residual risk is recorded honestly rather than smoothed over. The rest of review 14 (A3, A4, A7, A8) is still open; this review does not change it.

## A. Must fix

| # | Issue | Change |
|---|---|---|
| A1 | **The default branch comes from `feature-flow.yaml` on the ticket branch.** The agent can write to that branch, so it can declare another branch as the default, and a merge into `main` is then judged by the weaker ticket policy. A floor must not depend on a file the governed party can edit | Take the default branch from the **code host** (the repository's `default_branch`), at merge time. If `feature-flow.yaml` disagrees, refuse and record it. Add a test: an agent edits `feature-flow.yaml` to name another default branch, and a merge into `main` is still refused without the F1 approvals |
| A2 | **The App keeps Administration, so it can edit or delete the ruleset.** The residual risk in `review-14-a2.md` is correct. Creating the ruleset needed the permission; running the engine does not | **Parked by the product owner, 2026-10-29; to be done shortly.** Do not act on it yet. The intended change: remove Administration from the App, show that a ruleset `PUT` with the installation token is refused, and record a design rule that code host protection for default branches is configured by an owner or admin identity the engine never holds, with the engine only checking that the protection exists. Until then, keep the residual risk visible in `docs/16` |
| A3 | **The agent grant still includes `pull_requests:write`** (`review-14-a2.md` case 2). Review 14 A4(b) asked for `contents:write` only, with PR, checks and merge calls made by the engine | Narrow the agent grant as A4(b) asked, and show an agent-token PR creation refused |

## B. Should fix

| # | Issue | Change |
|---|---|---|
| B1 | The decision ledger is a local file, and `Human` is whatever the writer says | Acceptable for the spike. Record in `docs/01` that a decision's actor kind and roles come from the API's authenticated identity, never from the request body |
| B2 | The reproduction section commits a local key path (`D:\Users\...\keys\...pem`) | Not a secret, but use a placeholder or an environment variable name in committed docs |
