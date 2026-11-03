# M5: source control, testing and the first real ticket

Results of building M5 (`docs/11`, M5). Each builder writes its own section.

## Providers

The Git VCS provider's catch-up and merge, the GitHub code host and GitHub Actions CI providers, and SP4's race
scenario as a test. Branch `m5-providers`, pull request #5 into `m5`. Judgement calls are LOG 108 to 117,
unreviewed.

### What was built

| Part | What |
|---|---|
| Git (`GitVcs`) | `CatchUpAsync` and `MergeAsync`, built in the bare clone cache (`git merge-tree --write-tree`, `git commit-tree`) and pushed expecting the revision read (`--force-with-lease=<ref>:<expected>`). DR18, check whether a refused merge already landed: done before merging and again after a refused or lost push, by the tested revision or the exact `Feature-Flow-Merge: <key>` trailer. Branch reset for `clean_up` is the existing expected-revision push |
| GitHub code host (`GitHubCodeHost`) | Every method; each maps the success statuses GitHub documents, anything else is `unexpected_response` (a work error, never retried). Protection from the branch and its rulesets, without `administration` (F5, the engine never holds it). Pull requests carry the key as a hidden marker and are adopted by it. Reverts are GitHub's own (GraphQL). Checks name their workflow file as their suite |
| GitHub Actions (`GitHubActionsCi`) | Dispatch on the default branch, so the workflow definition is the reviewed one, with the revision as `commit_sha`; a hidden claim reference per key records the run, so a retry adopts it; the claim is deleted once the run is seen completed (LOG 112, revised after review). `GetExpectedRunsAsync` from the workflow files' triggers. Results are the artifacts' TRX, JUnit and SARIF files, unparsed |
| Shared client (`GitHubApi`) | One pooled connection handler (DR18's "reuse connections", DR20's trust store); engine tokens from the credential broker, per repository, cached; docs/06 error classes |
| Registration | `code_host: github`, `ci: github_actions`. The engine's broker ceiling gains `actions: write` (LOG 114) |
| SP4 (`MergeRaceTests`) | SP4's loop through the provider: catch-up, re-test if it changed the branch, merge expecting the parent revision recorded at the catch-up, back to catch-up on `base_moved`. One worker and clone cache per ticket, one local bare repository |

Fixes found on the way, all in the Git provider: the cache is named with `GIT_DIR`, because git refuses an
implicit bare repository where `safe.bareRepository` is `explicit` (it is on this machine, so M4's cache would
have failed here); `ResolveAsync` creates the cache folder; a push of the revision a reference already has is
`base_moved` unless expected (git skips the lease when nothing changes).

The build plan's "204 for an empty merge" is covered by the VCS: a catch-up with nothing to merge succeeds unchanged
(contract case), since merges go through the VCS, not the code host (coordinator decision on LOG 107).

### Checks

| Check | Result |
|---|---|
| `VcsContract` over the Git provider, local bare repository (`GitVcsContract`, default category) | 17 passed, including a new case: a reset is refused once the branch moved on. The new case also passes against the in-memory fake |
| Git provider tests (`GitVcsTests`) | 6 passed. Two workers run the same merge at once, held by a `pre-receive` hook so the second push is refused: both succeed and the change lands once. A catch-up whose work branch is moved between its fetch and its push (an internal test hook) is refused and the other commit stays |
| GitHub tests over recorded responses (`GitHubCodeHostTests`, `GitHubActionsCiTests`) | 77 passed: each endpoint's successes, the error classes (401, 403, 403 with an exhausted limit, 404, 409, 422, 429, 5xx), an undocumented success, adoption by marker, trigger idempotency, artifacts, 19 trigger-evaluation cases |
| SP4 S1, 3 tickets forced to collide | 3 refusals (`base_moved`); all 3 merged with 1, 2 and 3 merge executions; no merge lost |
| SP4 S2, 5 tickets, natural timing (re-tests 0.1 to 0.6 s, SP4's 2 to 8 s scaled down), race allowance 10 (LOG 61) | 10 refusals; all 5 merged; executions 4, 3, 2, 5, 1; no merge lost |
| SP4 S3, 4 tickets forced, allowance 3 | 6 refusals; 3 merged; 1 exhausted after 3 executions, its file absent from the parent |
| SP4 claim checks, every scenario | C1 no lost merge (each merged ticket's tested head is in the parent; the parent's first-parent history is exactly the merges), C2 nothing untested (each merge's parents are the recorded parent revision and the last tested head), C3 every merge names the caught-up, tested head and stays within the allowance, C4 every ticket ends merged or exhausted |
| SP4 control N1b, plain forced push, natural timing | All 5 tickets reported merged; **4 merges lost**. Kept as a test asserting the loss |
| Default suite (`Category!=Integration&Category!=Live`, Release) | 558 passed, 0 failed (431 at `1189a6b`; 528 before the review round, 532 after the first review decisions) |

### Controls

Each breakage was made, the tests run, and the change reverted.

| Breakage | Tests that failed |
|---|---|
| Git push ignores its expected revision (plain `--force`) | 2 contract cases |
| Merge skips the already-landed check | 2 (merge and squash retry) |
| No re-check after a refused merge push | The two-worker test |
| Merge accepts a tested head that lacks the target | 1 contract case |
| Trailer matched as a substring | The key-prefix test |
| Catch-up pushes without the lease | At first **none**: no case could move the branch between the provider's fetch and its push. After review, a test hook does, and the new catch-up test fails |
| Code host: no adoption by marker | 1 |
| Code host: any 2xx accepted | 1, after fixing the test: it first sent a body without the field, so it failed for the wrong reason and passed by construction |
| Actions: a retried trigger dispatches again (first, tag version) | 1 |
| Actions: branch filters ignored | 4 trigger cases |
| Close comments on a closed pull request | 1 |
| Actions: a status poll that sees the run completed keeps its tag (tag version) | The tag-removal test |
| Actions (default-branch version): dispatch on the claim instead of the default branch | 1 |
| Actions: no adoption from the claim | 3 (adoption, and both reused-key cases) |
| Actions: no claim clean-up on a status poll | 1 |
| Actions: no `commit_sha` requirement | 1 |
| SP4: no re-test after a changing catch-up (N2) | S1, S2, S3 (C3) |
| SP4: merge commit without the tested parent | S1, S2, S3 (C1: the tested head is not in the parent) |
| SP4: expected-revision push replaced by plain forced push (N1b) | C1, 4 of 5 merges lost |

A flaky first version of the two-worker test was caught on the way: with its two starts 0.7 s apart, both workers
sometimes built byte-identical merge commits in the same second, so the second push was a no-op and the re-check
never ran. The starts are now 1.1 s apart; five reruns passed.

### Live runs

Against `ai-feature-flow/feature-flow-example` through the GitHub App, on 2026-11-03. Each test works under its own
prefix from an orphan base (LOG 116), so the fixture's `main` is never written.

| Run | Tests | Result |
|---|---|---|
| 1 (development) | Code host contract | 13 passed in 3 min 19 s |
| 2 (final before review) | Code host and Actions contracts | **18 passed** in 3 min 17 s. Actions run 41540354167 (`unit.yml`, dispatched on its tag) completed with success and its TRX file was read; the retried trigger adopted run 41540462180 rather than dispatching again |
| 3 (after review: tag removed on a status poll) | Code host and Actions contracts | **18 passed** in 3 min 10 s. Run 41543579645 succeeded and its TRX was read; the retried trigger adopted run 41543691353 |
| 4 (after the steer: dispatch on the default branch, claim references) | Code host and Actions contracts | **18 passed** in 3 min 12 s. Run 41545035950, dispatched on `main` with the revision as `commit_sha`, succeeded and its TRX was read; the retried trigger adopted run 41545124333 from its claim 1.7 s later, before the run list could show it |

After each run the fixture had only `main`, still at `5dd0108`, no `ff-ci/` tag or `refs/feature-flow/ci/` reference, and no open pull request of these tests; the
trigger test's unfinished run was cancelled. GitHub pull requests can only be closed, not deleted, so the tests'
closed pull requests (#15 to #23 and those of run 2) stay as history.

The fixture needed no change: `unit.yml` already has `workflow_dispatch` (inputs `commit_sha`, filled by the
provider) and uploads a TRX file.

### Review decisions (2026-11-03)

| LOG | Decision | What was done |
|---|---|---|
| 109 | One HTTPS connection per `git` command is enough for version 1 | Nothing to change |
| 110 | Rulesets are enough, but unreadable classic review rules must read as "unknown" | `BranchProtection` cannot say "unknown" without giving a field a magic value, so a change to the frozen record is proposed in the providers report. Until then a classically protected branch reads as protected with no required approvals |
| 112 | Remove the trigger tags if the dispatch API's run id allows; then (steer from the independent review) dispatch on the default branch so the workflow definition is reviewed code | Tags first kept: a run does not expose its inputs and the run list lagged a dispatch by 7.6 s, so adopting by key from the list could start a second run (fixture pull request #33, a workflow showing the key in its `run-name`, needed a person's review and was closed). Then rebuilt as steered: dispatch on the default branch with `commit_sha`, idempotency by a hidden claim reference that records the run id, so a retry adopts at once without the list. No tags |
| 117 | An engine-level SP4 is deferred to integration | Noted |
| (lease) | Add a test of the catch-up's lease if cheap | Done: an internal hook before the push moves the branch; control fails it |

### Independent review of PR 5 (2026-11-03)

Verdict was changes-required, with one blocker. Each finding was fixed in its own commit (8 and 9 together), with a
test, and a control that broke the fix and saw that test fail. LOG 118 and 119 record them.

| # | Finding | Fix | Test | Control |
|---|---|---|---|---|
| 1 (blocker) | DR18's trailer check counted any commit with the key, so another run's agent commit could make a merge read as landed | A trailer commit counts only if its tree is the tested revision's | The reviewer's probe: run 1 merges an agent commit carrying run 2's key; run 2's squash still lands | Without the tree match the probe fails |
| 2 | Fixed scratch references and a per-instance lock on a cache shared by every worker | Scratch references per operation, deleted after; a static gate per cache and a lock file across processes | Two instances, one cache, ten rounds of concurrent catch-ups; an operation waits while another holds the lock file | Fixed names fail the first; no clean-up fails it; no lock file fails the second. Per-operation names alone pass the first, which is why the second exists |
| 3 | git auth faults and HTTPS 401, 403, 429 misclassed; a missing App permission retried forever as infrastructure | git: 401 `unauthenticated`, 403 `forbidden`, 429 `rate_limited`, before `unreachable`. REST 403 "Resource not accessible by integration": `rejected`, named a configuration fault | 7 cases on git's messages; protection read with the permission missing | Without the rules 4 cases fail; without the REST rule its test fails |
| 4 | No provider-level test of the leases | Tests that move the branch after the read and before the push (internal hook). A remote `pre-receive` hook cannot: git refuses reference updates in its quarantine (probe) | Catch-up and merge each refused with `base_moved`, the other commit kept | A plain `--force` push fails both |
| 5 | The trigger ran an agent-editable workflow with the repository's secrets | Done before the review reached it: dispatch on the default branch with `commit_sha`, a hidden claim reference for idempotency. The remaining `on: push` risk is recorded in LOG 119, with `permissions: contents: read` recommended | (as before) | (as before) |
| 6 | Artifacts downloaded whole, in memory, with a 30 s timeout, uncapped | Streamed downloads, 5 min, byte caps; large artifacts skipped; only result entries read, capped while decompressing | Large artifact skipped; a download larger than stated refused; an oversized result file refuses the read while a large non-result file is not read; the capped copy | Each removed cap fails its test |
| 7 | `scm.revert` of an engine squash always failed | Revert built from the squash commit with `POST /merges`; no interface change | Squash revert, no squash found, conflict; and Live on the fixture | Without the tree or the trailer match a decoy is taken, 2 fail |
| 8 | Reads used a write token | Read-scoped tokens for reads | Scope of a read and of a write | Reads with the write token fail |
| 9 | Token could go to any absolute URL; revisions unescaped | Only URLs under the API base; full SHAs and numeric run ids only | Token not sent elsewhere; malformed revisions and run ids make no call | Each guard removed fails its test |
| 10 | git inherited the worker's configuration | No system, global or inherited configuration; no credential helper or hooks path | A global configuration rewriting the remote's URL does not reach the provider | Without `GIT_CONFIG_GLOBAL` it fails |
| 11 | Test-only switch in production | Accepted as is (coordinator) | | |

Counts after the round: Git provider tests 35, GitHub default tests 98, default suite 558, SP4 4. **Live run 5**
(final): **19 passed** in 3 min 11 s. Actions run 41549262375 ran on `main` with the revision as `commit_sha` and
succeeded, its TRX read; the retried trigger adopted run 41549367436; pull request 57 reverted an engine-style squash of
pull request 55. Afterwards the fixture had only `main` (`5dd0108`) and another builder's pull request 50.

### Coordinator decisions, second round (2026-11-03)

| Decision | What was done | Test and control |
|---|---|---|
| LOG 110: add `ReviewRulesKnown` to `BranchProtection` (approved additive change) | `bool ReviewRulesKnown = true`; the GitHub provider sets it false when the branch has classic protection (`protection.enabled`), whose review rules need administration. The fake keeps the default | Classic protection reports unknown; always-true fails it |
| LOG 112: default-branch dispatch, run id from `return_run_details`, run-name search after a lost answer | Already dispatching on the default branch without tags; a claim with no run recorded now polls for 15 s for a dispatched run whose `run-name` carries the key, records and adopts it, then dispatches again. Results tie to the revision through `CiRunRef.Revision`; `ci.checks` reads push and pull-request runs, which report on the revision | Adoption by run-name; dispatch again when none; no search or a substring match fails the adoption test |
| Fixture pull request 33 (`run-name` with the key) may be merged once green | Reopened. It has no checks, and `main`'s ruleset requires one approving review with no bypass, so it awaits a person | |

Live run 6, after these: **19 passed** in 3 min 16 s (CI run 41552464251 succeeded with its TRX read; the retry adopted
41552570629; pull request 68 reverted a squash of 66). Default suite 562 passed.

### Ruleset protection on the fixture (actions builder's note, 2026-11-03)

The fixture's `main` is protected only by a ruleset, visible only through `GET /repos/{owner}/{repo}/rules/branches/main`.
`GetBranchProtectionAsync` already reads that endpoint; a new Live test (read-only) asserts the fixture's `main` reads as
protected, pull request required, 1 approval, review rules known. It passed; with the rulesets ignored (control) it
failed.

### Re-check of PR 5 (approve-with-fixes, 2026-11-03)

| # | Fix | Test | Control |
|---|---|---|---|
| R1 claims leaked | The default branch and inputs are checked before claiming; a claim is a tag object dated when made; a dispatch refused as a work error removes it; an unrecorded claim older than 3 h is reclaimed; a replaced run loses its `run-<id>` index | Check order; no claim for a workflow without `commit_sha`; 422 removes, 500 keeps; stale claim reclaimed without searching | Claiming first, keeping the claim, no reclaim: each fails its test |
| R2 adopted runs unverified | An adopted run must be the job's workflow, `workflow_dispatch`, on the default branch, created after the claim and the tested commit | Five forged-run cases; an old run carrying the key | No verification fails all six |
| R3 | `ReviewRulesKnown`'s consumer is check 24, the actions builder's | | |
| R4 | docs/06 s1.1: a missing permission's 403 is `rejected`, a configuration fault | | |
| R5 | The CI workflow contract in docs/06 s3.5 (dispatch from the default branch; `commit_sha` checked out; key in `run-name`; result artifacts; `permissions: contents: read`), pointed to from docs/07 | | |
| R6 hung git | Each git command has `CommandTimeout` (default 5 min); the process tree is killed and the operation fails with `timeout`, releasing the cache | A push held 20 s by a hook times out in 2 s and the cache is free | No timeout fails it |
| PR 6 re-check | `HttpRequestException`, `IOException`, `SocketException` (and disk, access, corrupt-zip failures) come back as results, never exceptions | 3 transport kinds for API, download and broker; corrupt artifact; IO, access, socket inside a merge; an uncreatable cache | Each narrower catch fails its cases |

The fixture changed meanwhile: its ruleset now requires no approvals (the Live test reads the count instead of
assuming 1), pull request 50 was merged, and so was 33, the dispatch-only `ff-contract.yml` whose `run-name` shows the
key. A new Live test uses it: a claim with no run recorded, and a run dispatched as if its answer was lost; the provider
finds it by run-name and adopts it (control, live: without the search a second run starts).

Counts: Git provider tests 40, GitHub default tests 117, default suite 584, SP4 4. **Live run 8 (final): 21 passed** in
3 min 12 s (CI run 41561226342 succeeded with its TRX read; lost-answer run 41561223496 adopted; retry adopted
41561328439; pull request 90 reverted a squash of 88). Afterwards the fixture had no claim reference, no run in progress
and no open pull request.

### Open questions

1. `BranchProtection` "unknown" review rules: see the proposal in the providers report.
2. LOG 112: a claim whose run was never recorded (the dispatch's answer lost) dispatches again. Acceptable for version 1?

### Follow-ups

- An engine-level SP4 test through the actions builder's steps, at integration.
- `GetExpectedRunsAsync` does not evaluate job-level `if:` conditions; a workflow whose every job is skipped still
  counts as expected.
