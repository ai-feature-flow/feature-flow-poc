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
| GitHub Actions (`GitHubActionsCi`) | Dispatch on a tag `ff-ci/<hash of key>` at the revision; the tag is the idempotency claim and is deleted once the run is seen completed (status poll or results read). `GetExpectedRunsAsync` from the workflow files' triggers. Results are the artifacts' TRX, JUnit and SARIF files, unparsed |
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
| GitHub tests over recorded responses (`GitHubCodeHostTests`, `GitHubActionsCiTests`) | 75 passed: each endpoint's successes, the error classes (401, 403, 403 with an exhausted limit, 404, 409, 422, 429, 5xx), an undocumented success, adoption by marker, trigger idempotency, artifacts, 19 trigger-evaluation cases |
| SP4 S1, 3 tickets forced to collide | 3 refusals (`base_moved`); all 3 merged with 1, 2 and 3 merge executions; no merge lost |
| SP4 S2, 5 tickets, natural timing (re-tests 0.1 to 0.6 s, SP4's 2 to 8 s scaled down), race allowance 10 (LOG 61) | 10 refusals; all 5 merged; executions 4, 3, 2, 5, 1; no merge lost |
| SP4 S3, 4 tickets forced, allowance 3 | 6 refusals; 3 merged; 1 exhausted after 3 executions, its file absent from the parent |
| SP4 claim checks, every scenario | C1 no lost merge (each merged ticket's tested head is in the parent; the parent's first-parent history is exactly the merges), C2 nothing untested (each merge's parents are the recorded parent revision and the last tested head), C3 every merge names the caught-up, tested head and stays within the allowance, C4 every ticket ends merged or exhausted |
| SP4 control N1b, plain forced push, natural timing | All 5 tickets reported merged; **4 merges lost**. Kept as a test asserting the loss |
| Default suite (`Category!=Integration&Category!=Live`, Release) | 530 passed, 0 failed (431 at `1189a6b`; 528 before the review changes) |

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
| Actions: a retried trigger dispatches again | 1 |
| Actions: branch filters ignored | 4 trigger cases |
| Close comments on a closed pull request | 1 |
| Actions: a status poll that sees the run completed keeps its tag | The new tag-removal test |
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
| 3 (after review, CI provider changed) | Code host and Actions contracts | **18 passed** in 3 min 10 s. Run 41543579645 succeeded and its TRX was read; the retried trigger adopted run 41543691353 |

After each run the fixture had only `main`, still at `5dd0108`, no `ff-ci/` tag and no open pull request; the
trigger test's unfinished run was cancelled. GitHub pull requests can only be closed, not deleted, so the tests'
closed pull requests (#15 to #23 and those of run 2) stay as history.

The fixture needed no change: `unit.yml` already has `workflow_dispatch` (inputs `commit_sha`, filled by the
provider) and uploads a TRX file.

### Review decisions (2026-11-03)

| LOG | Decision | What was done |
|---|---|---|
| 109 | One HTTPS connection per `git` command is enough for version 1 | Nothing to change |
| 110 | Rulesets are enough, but unreadable classic review rules must read as "unknown" | `BranchProtection` cannot say "unknown" without giving a field a magic value, so a change to the frozen record is proposed in the providers report. Until then a classically protected branch reads as protected with no required approvals |
| 112 | Remove the trigger tags if the dispatch API's run id allows | **Kept.** The id comes back, but a retry must find the run by key: a run does not expose its inputs, so the key would have to appear in the workflow's `run-name` (the fixture's `unit.yml` does not show it; adding a test workflow that does needs a person's review on the fixture, pull request #33, now closed), and the run list showed a dispatched run only after 7.6 s, so a retry soon after a lost answer, or two triggers in a row, would start a second run. Creating a tag is an atomic claim. The tag is now also deleted when a status poll sees the run completed |
| 117 | An engine-level SP4 is deferred to integration | Noted |
| (lease) | Add a test of the catch-up's lease if cheap | Done: an internal hook before the push moves the branch; control fails it |

### Open questions

1. `BranchProtection` "unknown" review rules: see the proposal in the providers report.
2. LOG 112: a repository with an unfiltered `on: push` also runs on a trigger tag. Acceptable?

### Follow-ups

- An engine-level SP4 test through the actions builder's steps, at integration.
- `GetExpectedRunsAsync` does not evaluate job-level `if:` conditions; a workflow whose every job is skipped still
  counts as expected.
