# M5: source control, testing and the first real ticket

Results of building M5 (`docs/11`, M5). Each builder writes its own section; the summary and the consolidated
follow-ups at the end are the integration's.

## Summary

**Built.** The Git VCS provider (catch-up and merge in a bare clone cache, every push expecting the revision it
read), the GitHub code host and GitHub Actions CI providers; the ten M5 engine actions (`scm.*`, `ci.checks`,
`wait.condition`, `test.run`, `test.integration`, `analysis.run`, `repo.inspect`) with TRX, JUnit and SARIF
parsing; run-start checks 20, 23, 24 and 36, step-start checks 31 to 33; the repository manifest; `clean_up`'s
branch reset; `parent_branch` in the run request (LOG 137); and, in the integration, the engine-level SP4 test and a
short budget for `unauthenticated` and `forbidden` (docs/06 section 1.1). Judgement calls: LOG 107 to 146, approved by the product owner 2026-11-03.

**Done-when, met.** `ticket_delivery` ran on the fixture `ai-feature-flow/feature-flow-example` from start to an
engine squash merge into the non-default parent branch `ff/epic/m5-live` (run `run-2bcdfbd1e2e5`, merge `4444a11`),
with a real `haiku` planner, implementer and five reviewers, and CI test evidence for the merged head. The contract
suites pass for Git (local), GitHub and GitHub Actions (Live run 8, 21 passed). Details under "Integration".

**Verified by, met.** SP4 through the engine's own `scm.catch_up` and `scm.merge` (`EngineMergeRaceTests`): no lost
merge, nothing untested lands; control N1b (plain forced pushes) loses merges. F1: a standalone ticket asking for an
engine merge into the default branch is refused at run start, on the memory providers and on the fixture.

**Tests** at the integration's last commit: default suite 741 passed, integration 50 passed (local dev server and
Docker, with `safe.bareRepository=all` for the run, follow-up 17), Live 21 (providers) plus the live run. Controls: `tools/check_m5_actions_controls.py`, 59,
all hold.

## Providers

The Git VCS provider's catch-up and merge, the GitHub code host and GitHub Actions CI providers, and SP4's race
scenario as a test. Branch `m5-providers`, pull request #5 into `m5`. Judgement calls are LOG 108 to 117,
approved 2026-11-03.

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

---

## Engine actions and checks

Branch `m5-actions`, from `m5@1189a6b` (the interfaces and in-memory fakes, LOG 107).

### What was built

| Part | Where | What |
|---|---|---|
| Ten actions | `src/FeatureFlow.Engine/Actions/` | `scm.catch_up`, `scm.pull_request`, `scm.merge`, `scm.revert` (`SourceControl.cs`); `ci.checks`, `wait.condition` (`Waiting.cs`); `test.run`, `test.integration`, `analysis.run`, `repo.inspect` (`Testing.cs`). Dispatched by `RunActionAsync` when a real VCS and code host are configured; other actions stay on the stand-in (LOG 121) |
| Result parsing | `Actions/ResultFiles.cs` | TRX, JUnit and SARIF into the `Evidence.cs` records, one place for sandbox and CI tiers |
| Evidence | `Actions/Evidence.cs` | The JSON of `test_results`, `check_results`, `analysis_results`, `merge_record` (LOG 132) |
| Waiting | `RunActionAsync` | A wait is one read per activity attempt; an open wait fails retryably with the poll interval as the next delay, so it holds no worker (LOG 122) |
| Run-start checks 20, 23, 24 | `Activities.cs` `RepositoryChecksAsync`, `Validation/RunStartChecks.cs` | Read the default branch, its protection and `feature-flow.yaml` through the code host |
| Step-start checks 31, 33 | `Validation/StepStartChecks.cs` | 31: each M5 action's inputs; 33: `tier_tags` against the manifest read at the step |
| Step-start check 32 | `RunWorkflow.cs` | A consumed artifact no step produced stops the step (patch `m5-step-start`, LOG 127) |
| Repository manifest | `Model/Manifest.cs` | `feature-flow.yaml` as `docs/07` section 4 writes it; unknown keys refused |
| F1 setting | `Model/Configuration.cs` | `policy.merge.default_branch.engine_may_merge`, carried in the run's policy |
| `clean_up` reset | `EngineActions.CleanUpAsync`, `RunWorkflow.cs` | Expected-revision push back to where the step started (patch `m5-clean-up`, LOG 128) |
| Agent step | `Engine.Agent/AgentActivities.cs` | Parent branch from the code host; manifest `setup` and `after_changes` run in the sandbox; start revision recorded (LOG 129, 135) |
| Memory sandbox | `Providers.Local/MemorySandbox.cs` | Scripted commands for engine tests, no container (LOG 136) |

`test.integration` is `test.run` with the `integration` tag across repositories; std's name works unchanged
(LOG 120).

### Checks

| What | Test | Result |
|---|---|---|
| Each action, with the in-memory world | `FeatureFlow.Engine.Tests`: `SourceControlActionTests` (16), `WaitingActionTests` (10), `TestingActionTests` (15), `ResultFileTests` (11 cases) | Pass |
| Checks 20, 23, 24 on std's `ticket_delivery` | `Engine.Tests/RunStartCheckTests` (5) | Pass |
| **F1**: a standalone ticket with `engine_can_merge: true` into the default branch is refused at run start | `RunStartCheckTests.F1_a_standalone_ticket_...` and, through the engine and CLI, `ForgeTicketTests.F1_through_the_engine_...` (run fails, detail cites check 23; with std's default `false` the same ticket starts) | Pass |
| std `ticket_delivery` to a merge into its parent branch, on the `memory` providers with the scripted runtime | `Integration.Tests/ForgeTicketTests.A_std_ticket_runs_...`: every step through `deliver.merge`; one squash commit on `main` holding exactly the tested head's tree, with the `Feature-Flow-Merge` trailer; the pull request closed with a link to the commit | Pass |
| **Done-when on memory**: a ticket started with `--parent-branch ff/epic/E-1` and `engine_can_merge: true`, with no F1 leave, lands there by an engine squash merge; `main` is untouched | `ForgeTicketTests.A_ticket_started_with_a_parent_branch_...` | Pass |
| `parent_branch` refused unless a branch name; F1 still applies when it names the default branch | `ApiTests.A_parent_branch_that_is_not_a_branch_name_is_refused` (5 cases), `RunStartCheckTests.F1_still_applies_...` | Pass |
| Check 32 | `Integration.Tests/StepStartCheckTests` (2, with a control), test library `libraries/m5` | Pass |
| Agent step: parent branch, `setup`, `after_changes` | `Engine.Agent.Tests/AgentManifestTests` (5) | Pass |
| Manifest reader, patterns, `engine_may_merge` | `Model.Tests/ManifestTests` (17 cases) | Pass |
| Unchanged behaviour | `TicketRunTests` (5), `EpicRunTests`, `StepFailureTests`, `ContinueAsNewTests`; replay tests | Pass |

Counts after the re-check's fixes and the merge of PR 5: default suite **728 passed, 0 failed**, on `m5@5af8fe8` (431 at `1189a6b`). Integration on a dev server: `ForgeTicketTests` 7, `StepStartCheckTests` 2, and the existing ticket, epic, step-failure and SP1 tests, 17 in all. Integration tests were run locally on a dev
server, not the Docker agent tests: the agent step's change with no code host keeps M4's behaviour, and CI runs them.

The engine tests found one bug of their own: `ci.checks` read the clock before awaiting the wait's start time,
so its first read saw a negative elapsed time and waited out a zero grace. Fixed; the tests then ran ten times
with no failure.

### Controls

`tools/check_m5_actions_controls.py` (needs `FF_TEMPORAL_ADDRESS`; no Docker). Each breakage was run, its tests
failed, and all passed again after the revert. **All 52 hold** (22 before the review, 23 added for it, 7 for the re-check; the review's controls are listed in the script under "Added for the review of PR 6").

| Breakage | Tests that failed |
|---|---|
| `require_tested` skipped | `A_head_without_passing_test_evidence_is_not_merged` |
| `require_checks` skipped | `A_head_with_failed_checks_is_not_merged_...` |
| F1 not re-checked at merge time | `F1_at_merge_time_turns_..._into_an_action_request` |
| Check 23 skipped | 3 `RunStartCheckTests` F1 cases and `ForgeTicketTests.F1_through_the_engine_...` |
| Check 24 skipped | `Check_24_an_unprotected_default_branch_is_refused` |
| Check 20 not called | `Check_20_..._overrides_is_refused_...` |
| `ci.checks` ignores expected CI runs | `An_early_third_party_check_does_not_pass_while_an_expected_CI_run_has_not_reported` |
| `ci.checks` ignores a head change | `A_head_that_changes_while_checks_are_awaited_...` |
| Check 31 skipped | `Check_31_refuses_...`, `Wait_condition_..._takes_one_condition_only` |
| Check 33 skipped | `Check_33_a_tag_that_matches_no_tier_...` |
| Check 32 skipped | `Check_32_a_step_whose_consumed_artifact_was_never_produced_...` |
| A carried result adopted for another head | `A_head_already_tested_..._adopts_the_result_...` |
| TRX failures counted as passes | `TRX_counts_outcomes_...`, `Failed_tests_are_failed_...` |
| Analysis ignores the baseline | `Analysis_fails_on_new_issues_against_the_baseline_...` |
| `clean_up` does not reset the branch | `A_clean_up_resets_the_work_branch_...` |
| `clean_up` leaves hidden references | the same test |
| Actions' parent branch hard-coded `main` | `A_pull_request_is_opened_into_the_default_branch_read_from_the_code_host_...` |
| Agent's parent branch hard-coded `main` | `The_parent_branch_is_the_default_branch_the_code_host_names` |
| Agent `setup` not run | `Setup_runs_before_the_agent_...`, `A_failing_setup_command_...`, `After_changes_does_not_run_...` |
| Agent `after_changes` not run | `Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_...` |
| A start's `parent_branch` dropped by the API | `ForgeTicketTests.A_ticket_started_with_a_parent_branch_lands_there_...` |
| Any named parent branch skips F1, even the default | `F1_still_applies_when_a_start_names_the_default_branch_as_the_parent_branch` |

### The live run (M5's done-when): how to

The done-when's merge is the engine's, into a parent branch that is not the default: how a ticket inside an epic
lands. A run names that branch with `parent_branch` (LOG 137); a standalone ticket into `main` stays a person's merge.

**State of the fixture** (`ai-feature-flow/feature-flow-example`)
- `feature-flow.yaml` in the current format: fixture pull request 50. Both tiers run in CI (`unit.yml`,
  `integration.yml`), because the M4 sandbox image has no .NET SDK; no setup commands; no analyser. Its CI passed.
  **It needs one approving review before it can merge** (see below); until then the old manifest is on `main` and
  every step that reads it stops for a person.
- `main` is protected by the ruleset `protect-main`: pull request with one approval, last-push approval, no
  force-push, **no bypass actors**. It existed before M5 (2026-10-29). GitHub's classic branch-protection API reports
  `main` as unprotected (404); the GitHub code host must read rulesets (`GET /repos/{owner}/{repo}/rules/branches/{branch}`)
  for check 24 to pass.

**Configuration**
- Installation as M4's live tests, plus M5's providers: `vcs` git, `code_host` github, `ci` github_actions,
  `sandbox` docker, `agent_runtime` claude_code; `FF_SECRETS_FILE` and `CLAUDE_CODE_OAUTH_TOKEN` as in
  `CONTRIBUTING.md`.
- Organisation: std as its library. No `engine_may_merge` is needed: the merge target is not a default branch.
  Optionally `approval: [{ match: "ticket.spec_approval", roles: [approver], auto: true }]`, else answer the spec
  approval with `ff run answer`.

**Commands**
```bash
# The parent branch, from main (the engine does not create a named parent branch).
GH_TOKEN="$SPARKKZ_NZ_GITHUB_TOKEN" gh api repos/ai-feature-flow/feature-flow-example/git/refs \
  -f ref=refs/heads/ff/epic/m5-live -f sha="$(GH_TOKEN="$SPARKKZ_NZ_GITHUB_TOKEN" gh api repos/ai-feature-flow/feature-flow-example/git/ref/heads/main --jq .object.sha)"
ff run start std:templates/ticket_delivery --param ticket_id=M5-LIVE-1 --param engine_can_merge=true \
  --repo ai-feature-flow/feature-flow-example --parent-branch ff/epic/m5-live
ff run wait <run id> --timeout 3600
```

**Expected end state**
- The run is `completed`; its timeline passes `baseline.tests`, `implement`, `verify.tests`, `deliver.open_pr`,
  `deliver.checks`, `deliver.catch_up`, `deliver.merge`, with CI tier results (`tier unit ...: n passed`).
- `ff/epic/m5-live` has one new squash commit whose tree is the tested head's, with the trailer
  `Feature-Flow-Merge: <run id>/deliver.merge`; `main` is unchanged.
- The pull request `ff/ticket/M5-LIVE-1` into `ff/epic/m5-live` is closed with a comment linking the squash commit.
- Clean up afterwards (live runs leave no references): delete `ff/ticket/M5-LIVE-1`, `ff/epic/m5-live`, and any
  `refs/feature-flow/wip/*` and `refs/feature-flow/superseded/*` the run made.

A control for F1 on the live repository: the same start without `--parent-branch` is refused at run start, check 23.

### Review of PR 6

An independent review found two blocking defects in the "nothing untested lands" floor, and eleven more. Each fix
has tests and a control (`tools/check_m5_actions_controls.py`).

| # | Finding | Fix |
|---|---|---|
| 1 | **Blocking.** `scm.merge` took evidence by type name, so an agent could write its own "passed" through a repository library's `test_results` type | Evidence counts only from the right engine action (`test.run` for `test_results`, `ci.checks` for `check_results`) and a type marked `evidence: true`. `test.integration`'s does not satisfy `require_tested` (14) |
| 2 | **Blocking.** A result file with no tests, all skipped, or an aborted run read as passed | A tier that ran no tests fails; every TRX outcome but `Passed` and `NotExecuted` fails; a TRX run outcome other than completed fails; JUnit suite-level errors and counts are read |
| 3 | A malformed SARIF threw an exception Temporal retried for ever | It fails to parse (intervention); any unexpected exception in an action raises an intervention |
| 4 | A deny-list of failing check conclusions let `startup_failure` pass | Allow-list: `success`, `neutral`, `skipped` pass; unknown conclusions raise an intervention |
| 5 | `poll_every: 0s` could make a wait spin | Check 31 sets a 5 s floor |
| 6 | A wait holds up continue-as-new | **Not fixed**: a known limit, LOG 139, M6 must fix |
| 7 | The scope and prompt rendering were not behind a patch marker | Behind `m5-step-start` |
| 8 | Key rules passed by construction; no history held the M5 markers | Tests for evidence of another repository (B1), conclusions (B2), the latest execution's start (B3); std's `ticket_spec_rejected` `clean_up` on the memory providers; history `tests/histories/ticket_clean_up.json` holds both markers |
| 9 | A `clean_up` work error was retried for ever | It raises an intervention at the step it was for; only infrastructure retries |
| 10 | The agent wrote both the tier command and the result file | Evidence tiers and the analyser come from the parent branch's manifest (LOG 138); result paths are removed and checked gone first; `test_results` records the manifest hash |
| 11 | A `branch_pattern` rendering to `main` would put the engine's writes on `main` | Run start and every writer (actions, `clean_up`, the agent step) refuse a work branch equal to the parent or default branch |
| 12 | The manifest reader was duplicated | One `RepositoryManifest.Parse`. The sandbox polling loop stays: the shared helper is in a frozen file |
| 13 | A heartbeat timeout on stand-in calls; `run.parent_branch` still `main` | Whether actions run against providers is fixed at run start, and only then is the heartbeat set; run start's default branches reach the core, so `run.parent_branch` is right |

### Re-check of PR 6

Verdict approve-with-fixes; each fix has tests, and a control where testable.

| # | Fix |
|---|---|
| R1 | `HttpRequestException`, `IOException`, `SocketException` and `TimeoutException` escaping a provider are infrastructure errors, retried |
| R2 | LOG 139's must-fix is a row of `docs/11`'s M6 section |
| R3 | A `clean_up` aimed at a container is its first step's; one aimed at a decision is refused at load (check 8) and at a person's go-to (LOG 140) |
| R4 | Evidence sandboxes run the parent branch's `setup` too; the agent's sandbox keeps the head's (LOG 138 updated) |
| R5 | The work-branch refusal is check 36 in `docs/09` (LOG 141) |
| Check 24's "review rules unknown" warning | After merging `m5` with PR 5: protection whose `ReviewRulesKnown` is false passes check 24 with a warning, and run start's warnings now reach the timeline (they were dropped), behind `m5-start-warnings` (LOG 142) |

### Deviations and judgement calls

LOG 120 to 142, approved by the product owner 2026-11-03. These were marked for a look: **138** (evidence tiers from the parent branch's manifest), **139** (waits hold up a hand-over), **120** (`test.integration` starts no services), **123** (the
action request is watched on the code host; no decision record yet), **137** (a run request may name its
`parent_branch`; approved 2026-11-03). LOG 121 keeps the
stand-in for runs with only fake providers.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | Check 23 at patch time; the merge-time re-check covers it meanwhile |
| 2 | The action request's decision record and an API answer for it (M7) |
| 3 | `test.integration` starting services from `integration_environment` |
| 4 | `analysis.run` with `sonarcloud`; `wait.condition` on a tracker issue |
| 5 | The carried test result by tree identity, not only by revision |
| 6 | **M6 must fix**: a wait holds up the run's hand-over (LOG 139) |
| 7 | A person ending a wait by hand with `met` or `unmet` (`docs/04` section 8.2) |
| 8 | Inbound events to wake waits early; waits poll every 30 seconds |
| 9 | The GitHub code host must read rulesets as well as classic protection (the fixture's `main` is protected only by a ruleset), with the App's permissions; a 403 there is an infrastructure error and run start retries |
| 10 | A run start does not check that a named `parent_branch` exists; the first step that touches the repository stops for a person |
| 11 | `wait.condition: approved` accepts any identity's approval, the author's or the engine's included (M7, people and policy) |
| 12 | A tier delegated to CI runs the workflow file on the head, which the agent can edit; the engine cannot clear CI's result files first. A green `ci.checks` is the independent backstop |

---

## Integration

Branch `m5`, from `m5@932397d` (PR 5 and PR 6 merged). Judgement calls LOG 143 to 146, approved 2026-11-03.

### Engine-level SP4 (LOG 117's follow-up, LOG 143)

`Integration.Tests/EngineMergeRaceTests` runs SP4's scenarios through `EngineActions`' own `scm.catch_up` and
`scm.merge`, as the workflow's activity calls them, over the real Git provider and one local bare repository. Each
ticket is its own worker (engine, clone cache, record store) and merges into a run-named parent branch, not the
default. The re-test records passing `test.run` and `ci.checks` evidence for the exact head, which is what
`scm.merge` reads. SP4's pre-registered checks are reused, with the departures listed below the table: C1 no lost
merge, C2 nothing untested lands (each merge's parents are the parent revision the harness read after the ticket's
last catch-up and the last tested head, the engine's merge record agrees, and its tree is exactly the tested tree), C3
loops well formed, C4 termination.

| Scenario | Result |
|---|---|
| S1, 3 tickets forced to collide | 3 refusals (`base_moved`); all merged with 1, 2 and 3 merge executions; none lost or untested |
| S2, 5 tickets, natural timing, race allowance 10 | 10 refusals; all 5 merged (executions 3, 5, 2, 4, 1) |
| S3, 4 forced, allowance 3 | 6 refusals; 3 merged; 1 exhausted after 3, its file absent from the parent |
| N2, no re-test after a changing catch-up | The engine refuses 2 merges as `checks_failed`; nothing untested lands |
| **Control N1b**, plain forced push (a test asserting the loss) | All 3 report merged; **2 merges lost** |

Each push to the parent first waits a moment (the provider's test hook), standing for the network between the
engine's read and its push. Without it the engine's merge reads the parent just before pushing, so N1b's plain
pushes rarely overlap: the engine narrows the race window but only the lease closes it.

Departures from SP4's pre-registered checks (`docs/spikes/sp4-results.md` section 3):

- (a) **N1b's scenario.** SP4's N1b is S2's shape (5 tickets, natural timing). Here it is S1's (3 tickets forced
  together), with the push delay staggered 0.4 s per ticket, so each plain push lands after the one before and
  overwrites it. With natural timing the engine's late read of the parent makes overlaps rare, and the control would
  not reliably show a loss.
- (b) **S1's allowance** is 10, LOG 61's race allowance, as in the provider-level `MergeRaceTests`; SP4 used 3. With
  3 tickets, the most any ticket needs is 3 executions, so the result is the same. S3 keeps SP4's 3.
- (c) **C2's recorded revision** is the parent as the harness read it right after each catch-up, as SP4 defines it.
  The engine's catch-up does not report the revision, so the harness reads it. If the parent moves in between, the
  head lacks the new revision and the engine must refuse the merge. The engine's own merge record is checked to agree.
- (d) **C3 holds by construction**: the harness, not the workflow, routes `base_moved` back to catch-up and orders
  catch-up, re-test and merge, so its violation check can only fire under N2, where it is turned off. The workflow's
  routing is covered by std's workflow tests (`ticket_base_moved`, `ticket_races_exhausted`), not here.

| Source control (script) | Tests that failed |
|---|---|
| `scm.merge` reads no evidence | N2: an untested head lands |
| `scm.merge` reports a refused merge as merged | S1, S2, S3 and N2 (C1) |

### A budget for `unauthenticated` and `forbidden` (LOG 144)

The engine retried both for ever as infrastructure. `ConfigurationFaults` now gives them 5 tries (waits 1, 2, 4,
8 s) inside one activity attempt; an engine action or a `clean_up` reset runs again whole, and run start's reads of the
code host share one budget (review of PR 7: per-read budgets over many repositories could outlast the activity's
timeout). A `clean_up` keeps the resets of earlier tries in its result. An action re-run whole repeats its earlier
work, at worst 5 times; LOG 144 bounds it. When the budget is spent an action raises an intervention naming the
configuration fault, a `clean_up` returns it as its error (an intervention at its target step), and run start
refuses the start. Other infrastructure errors still go to Temporal without limit. Temporal's attempt count cannot be
the budget, because a wait's polls are failed attempts too (LOG 122). **The agent step is not covered** (follow-up).

| Test (`Engine.Tests`) | Control (script) that fails it |
|---|---|
| Each fault stops the step after exactly 5 calls, naming the fault; recovery on the 5th call passes; `unreachable`, `rate_limited`, `server_error` are thrown after 1 call; a `clean_up` stops; run start refuses only once the budget is spent, and throws `unreachable` | No budget (9 cases fail); no retry before stopping (4 fail); run start with no retry (4 fail) |
| 8 refused repositories at run start make 12 calls in all (5, then 1 each), and 8 blocking diagnostics | A budget per read |
| A `clean_up` refused in its second repository still reports the first repository's reset | Events dropped when the budget is spent |

### The live run (M5's done-when)

Setup as "The live run: how to" above, on a Temporal dev server at port 8833, the three hosts from `hosts/`, model
`haiku`. The parent branch `ff/epic/m5-live` was made from `main` (`1610c66`) plus one commit with the ticket's text,
`docs/tickets/M5-LIVE-1.md`, because there is no tracker provider before M6 (LOG 146). The ticket: widget names at
most 100 characters (`WidgetStore.MaxNameLength`, `Add` throws, `POST /widgets` returns 400), with unit tests.

**F1 control** (`run-f31ca73fc419`): the same start without `--parent-branch` failed at run start, check 23: "deliver.merge
asks for an engine merge into ai-feature-flow/feature-flow-example's default branch main; the organisation does not
allow it". It created no branch.

**First attempt** (`run-3cf52e115886`, aborted). Baseline, plan, implement and the tests passed; then four of the five
reviewers read the change through `Bash` and opened at most one file with `Read`, so `changed_files_unread` stayed
above 0 and three exhausted their attempts. The engine's count was right (the record store shows the hook events);
std's review task never said which tool counts. Fixed in the task (LOG 145), the run aborted and its references
deleted, and the run started again.

**The run** (`run-2bcdfbd1e2e5`, **completed**, 124 timeline entries, 02:36 to 02:44 UTC, 8 minutes):

| Step | What happened |
|---|---|
| `baseline.tests` | Work branch `ff/ticket/M5-LIVE-1` created from `ff/epic/m5-live` at `14fb2de`; both tiers delegated to CI (runs 41567141694, 41567216286): unit 3 passed, integration 1 passed (by exit; it uploads no result file) |
| `baseline.analysis` | No analyser declared; passed |
| `plan.plan` | Planner (9 model calls) wrote requirements and a delivery plan from the ticket file; `artifact.validate` on the stand-in: valid |
| `approve_spec` | Auto-approved by the organisation's rule |
| `implement` | Implementer (26 model calls) committed `04121a2`: 5 files, +86 lines (the limit, the 400, two store tests and two endpoint tests through `WebApplicationFactory`); the commit went to `refs/feature-flow/wip/...`, then the work branch |
| `verify.tests` | Unit tier in CI (run 41567736364) at `04121a2`: **8 passed, 0 failed** |
| `verify.review` | Five reviewers, one per angle, each opened all 5 changed files with `Read` (`changed_files_unread=0`) at the first attempt; 13 findings, none blocking. `findings.record` on the stand-in |
| `verify.satisfaction` | Stand-in: valid |
| `deliver.open_pr` | Pull request #99 from `ff/ticket/M5-LIVE-1` into `ff/epic/m5-live` |
| `deliver.checks` | On `04121a2`: integration success, unit success (the push-triggered runs) |
| `deliver.catch_up` | Already contained the parent at `14fb2de`: unchanged |
| `deliver.merge` | **Squash `4444a113545c8e29fb0711978ded06acabc228c5`** into `ff/epic/m5-live`, by `feature-flow` |

Checked on GitHub afterwards: `4444a11` has one parent, `14fb2de` (the epic's head), the tested head's exact tree
(`d8cd4dc`), and the trailer `Feature-Flow-Merge: run-2bcdfbd1e2e5/deliver.merge`; `main` was still `1610c66`; pull
request #99 was closed with "Squashed into ff/epic/m5-live as 4444a113... by Feature Flow."

**Clean-up.** Deleted `ff/ticket/M5-LIVE-1`, `ff/epic/m5-live` and `refs/feature-flow/wip/run-2bcdfbd1e2e5/implement`
(and, after the first attempt, its branch and `refs/feature-flow/wip/run-3cf52e115886/implement`). No
`refs/feature-flow/superseded/*` or `refs/feature-flow/ci/*` existed (the CI claims are removed when a run completes).
Afterwards the fixture had only `main` (`1610c66`) and `ff/manifest-v1`, a branch from before this work, since deleted
by the coordinator (it had been merged), so only `main` (`1610c66`) remains; no open pull request and no workflow run
in progress. Pull request #99 stays closed. No sandbox container was left.

### Deviations

- The handover written after M5 is `docs/build/handover-2026-11-03-m6.md`, as `handover-2026-11-03.md` already exists for M4.
- The live run's ticket is a file on the parent branch, the spec approval is automatic, and plan validation and the
  findings ledger are M6's stand-ins (LOG 146).

## Simplification after M5

Approved by the product owner on 2026-11-03 under the development and decision guidelines (`CONTRIBUTING.md`); one
commit per item, recorded as LOG 147. Net lines are code, tests and controls, from `git diff --stat`.

| # | Removed | What still covers the risk | Net lines |
|---|---|---|---|
| 1 | CI dispatch idempotency (LOG 112): the claim reference, its tag record, the stale-claim reclaim, the run-name search, the adopted-run check, the trigger's key | Dispatch on the default branch with `commit_sha` required. The run id is recorded as the step's event, so retries poll it; a lost answer may start a duplicate run | -438 |
| 2 | Trigger matching (LOG 113): `GetExpectedRunsAsync`, branch, path and type filters | `ci.checks` waits for the target branch's required checks, `expected` names and every check on the head; `HasCiAsync` waits up to `grace` for a first check | -192 |
| 3 | The configuration-fault budget (LOG 144): 5 tries with backoff, run start's shared budget | The provider retries once with a fresh token; then an intervention, a refused start, or (new) the agent step stops for a person | -39 |
| 4 | The clone cache's cross-process lock file (LOG 118) | One cache folder per process; the in-process gate per clone and the scratch references | -22 |
| 5 | `ReviewRulesKnown` and check 24's warning (LOG 110, 142) | Check 24 still refuses an unprotected default branch; start warnings still reach the timeline | -27 |
| 6 | Check 36 in every writer (LOG 141) | Check 36 at run start; the work branch is fixed for the run | -94 |
| 7 | The manifest hash in test evidence and the same-hash adoption rule (LOG 138) | Tiers, analyser and setup from the parent branch's manifest; result paths cleared first | -2 |
| 8 | The per-entry and log download caps (LOG 119) | One 100 MB cap on every download | -12 |

Controls (`tools/check_m5_actions_controls.py`) for removed behaviour went with it; new ones: a CI run not recorded, a
required check not waited for, no wait for a first check, and configuration faults retried for ever by an action, run
start or the agent step, or not retried with a fresh token by the GitHub client. Two tests found passing by
construction were fixed: the oversized-download test (its download was not a zip) and a test of `test.run`'s CI
recording (adoption would have hidden a second trigger; it uses `test.integration`). The shared-cache concurrency
test passes with either the gate or the scratch references alone, and fails with neither. The fixture's
`ff-contract.yml` (fixture pull request 33) is no longer used and stays on the fixture. Follow-ups 2 and 15 below are
settled by items 3 and 2.

---

## Consolidated follow-ups

| # | Follow-up | From |
|---|---|---|
| 1 | **M6 must fix**: a wait (`ci.checks`, `wait.condition`, an action request) holds up the run's hand-over (continue-as-new) until it ends (LOG 139; a row of `docs/11`'s M6) | Actions review 6 |
| 2 | ~~The agent step has no configuration-fault budget~~ Settled by LOG 147: every caller stops for a person after the provider's one fresh-token retry | Integration |
| 3 | A re-run reviewer is not told which files it left unread; a correction turn naming them would save attempts (LOG 145) | Integration |
| 4 | Re-run the done-when with M6's real validators, findings ledger and tracker in place of the stand-ins and the ticket file (LOG 146) | Integration |
| 5 | Check 23 at patch time; the merge-time re-check covers it meanwhile | Actions 1 |
| 6 | The action request's decision record and an API answer for it (M7) | Actions 2 |
| 7 | `test.integration` starting services from `integration_environment` | Actions 3 |
| 8 | `analysis.run` with `sonarcloud`; `wait.condition` on a tracker issue | Actions 4 |
| 9 | The carried test result by tree identity, not only by revision | Actions 5 |
| 10 | A person ending a wait by hand with `met` or `unmet` (`docs/04` section 8.2) | Actions 7 |
| 11 | Inbound events to wake waits early; waits poll every 30 seconds | Actions 8 |
| 12 | A run start does not check that a named `parent_branch` exists; the first step that touches the repository stops for a person | Actions 10 |
| 13 | `wait.condition: approved` accepts any identity's approval, the author's or the engine's included (M7) | Actions 11 |
| 14 | A tier delegated to CI runs the workflow file on the head, which the agent can edit; `ci.checks` is the independent backstop | Actions 12 |
| 15 | ~~`GetExpectedRunsAsync` does not evaluate job-level `if:` conditions~~ Settled by LOG 147: it is gone; required checks decide | Providers |
| 16 | The fixture's integration tier uploads no result file, so it is judged by exit code and its tests cannot be claimed | Integration |
| 17 | The agent tests' own git helper (`AgentHarness`) opens the local remote as an implicit bare repository, so 6 of them fail where `safe.bareRepository` is `explicit` (this machine); they pass with it set to `all` for the test run, and on CI | Integration |
| 18 | Still open from earlier milestones: M4 12 (the sandbox network is unrestricted and reaches the Temporal frontend; before any shared deployment), M3 10 (a child that ends without signalling), M3 2 and 3 (a child's parameters and `consumes` across runs; M6 needs both) | Handover 2026-11-03 |

Done since they were listed: the engine-level SP4 (providers follow-up; above); rulesets read by the code host
(actions follow-up 9; the providers already read them, and a Live test asserts it).
