# SP4: lock-free merging under forced races

Status: plan and checks pre-registered 2026-11-01 (commit `7bec8cb`), before
any run; N1b added by amendment (commit `9a45bd4`) before it ran. Run the same
day against GitHub. **The claim holds**: in 8 runs no merge was lost, nothing
untested landed, and every loop terminated. But with natural timing, 2 of 5
concurrent tickets used up the merge allowance of 3 in every run and would have
escalated, although nothing was wrong with their work (section 5.3, LOG 61).

## 1. Claim under test

Spike 3 merged concurrent tickets under a merge lock. The design replaces the
lock (`docs/04` section 7.3, `docs/02` section 6):

| Step | Behaviour |
|---|---|
| `scm.catch_up` | Merges the parent branch into the work branch and records the parent revision it caught up to. `changed` is true if the work branch did not already contain the parent |
| Re-test | The standard fragment re-tests when `changed` is true. The test evidence names the exact head tested |
| `scm.merge` | Builds the merge commit from exactly the tested head and pushes it to the parent **with the recorded parent revision as the expected old value**. If the parent moved, the remote refuses: outcome `base_moved`, which routes back to catch-up |
| Allowance | Every `base_moved` loop re-enters `scm.merge`, which counts against its attempt allowance (3 by default). When it is spent the step returns `attempts_exhausted` and the run escalates |

The claim is that, with three or more tickets racing to merge into one parent,
this **loses no merge, lands nothing untested, and terminates**: race losers
loop and merge, and a branch that keeps losing is stopped by the allowance.
Risk R10 (concurrent work in one repository conflicts) in `docs/10` section 2.

## 2. What is built

`spikes/07-lock-free-merge`: a Python harness that runs each ticket's
catch-up, re-test and merge loop concurrently against the real fixture
repository on GitHub (`ai-feature-flow/feature-flow-example`).

| Part | Behaviour |
|---|---|
| Setup | Creates a parent branch `sp4/<run>/parent` from `main`, and one work branch per ticket, each adding one distinct file, so tickets never conflict (conflict resolution was evidenced in Spike 3). `main` is never written |
| Clones | Each ticket has its own clone, standing in for separate orchestration workers with their own clone caches. Operations run in that clone and push to GitHub |
| Catch-up | Fetches the parent, merges it into the work branch, pushes the work branch, records the parent revision |
| Re-test | Simulated: records the work head as tested and waits a set time. Runs when catch-up changed the branch, and before the first merge |
| Merge | Checks the allowance first. Builds a merge commit with first parent the recorded parent revision and second parent the tested head, then `git push --force-with-lease=<parent>:<recorded revision>`. A refusal from GitHub is `base_moved` |
| Forced races | A barrier makes every ticket still trying push its merge at the same moment, each round |
| Log | Every operation, revision, push result and outcome, per ticket, with timestamps |

Pushes use the SSH key that previous spikes used for the fixture, not the
engine's GitHub App identity. The race behaviour is the remote's and does not
depend on the identity.

## 3. Scenarios and pre-registered checks

| # | Scenario | Runs |
|---|---|---|
| S1 | 3 tickets, allowance 3, every merge round forced to collide | 3 |
| S2 | 5 tickets, allowance 3, natural timing: re-test takes a random 2 to 8 s, no barrier | 3 |
| S3 | 4 tickets, allowance 3, every round forced to collide, so one ticket must lose three times | 2 |

**Exercised:**

| # | Check | Threshold |
|---|---|---|
| E1 | GitHub refused merge pushes as `base_moved` | S1: 3 refusals per run (2, then 1); S3: 6 per run (3, 2, 1); S2: at least 1 across the three runs |

**Claim checks** (every run):

| # | Check | Pass |
|---|---|---|
| C1 | No lost merge | Every ticket reported `merged` has its tested head in the final parent. The parent's first-parent history from its setup revision to its final revision is exactly the engine's merge commits, one per merged ticket, with none missing or overwritten |
| C2 | Nothing untested lands | Each merge commit's second parent is the ticket's last tested head, and its first parent is the parent revision recorded at that ticket's last catch-up |
| C3 | Loops are well formed | Every refusal is followed by a catch-up, then a re-test if the catch-up changed the branch, then a merge. No ticket executes `scm.merge` more than its allowance |
| C4 | Termination | S1: all 3 tickets merged. S2: every ticket ends `merged` or `attempts_exhausted` within 10 minutes; exhaustions are counted. S3: exactly 3 tickets merged and 1 ended `attempts_exhausted` after 3 merge executions, with its file absent from the final parent |

**Negative controls.** Each must make the named check fail:

| # | Change | Must fail |
|---|---|---|
| N1 | S1 with a plain forced push (`--force`) instead of the expected-revision push | C1: concurrent pushes overwrite each other's merges |
| N2 | S1 with the re-test after a changing catch-up skipped | C2: a merge lands a head that was never tested |

**Amendment, made after N1 ran and before N1b ran.** N1 did not fail C1. With
the barrier, every forced push starts at the same moment and carries the same
old value, which the client read from GitHub when the push started; GitHub
refuses all but one (`cannot lock ref ... is at <new> but expected <old>`,
reproduced by hand on a throwaway branch). Git's push protocol already refuses
simultaneous overwrites. An overwrite needs a push that starts after the parent
has moved past the revision recorded at catch-up. Added N1b: S2 (natural
timing, 5 tickets) with `--force`; it must fail C1. N1 is kept as run.

## 4. Out of scope

Conflicts (Spike 3), real tests and CI, branch protection and merge queues,
squash merges, and the engine's identity and credential broker.

## 5. Results

Raw evidence per run in `spikes/07-lock-free-merge/runs/<scenario>-<variant>-<n>/`:
`summary.json` (checks, outcomes, the parent's first-parent history) and
`events.jsonl` (every catch-up, test and merge push, with revisions and
GitHub's reply). Each run's final parent is kept on the fixture as tag
`sp4/<run>/final`; its branches were deleted. Run with
`python3 merge.py <S1|S2|S3> <n> [--variant n1|n2] --work <dir>` in
`spikes/07-lock-free-merge`.

### 5.1 Main runs

| Run | Tickets | Refusals (`base_moved`) | Merged | Exhausted | Merge executions per ticket | Checks |
|---|---|---|---|---|---|---|
| S1 1, 2, 3 | 3 | 3 each | 3 each | 0 | 1, 2, 3 in some order | All pass |
| S2 1, 2, 3 | 5 | 9 each | 3 each | **2 each** | Exhausted tickets: 3 | All pass |
| S3 1, 2 | 4 | 6 each | 3 each | 1 each | 1, 2, 3, 3 | All pass |

- **C1 no lost merge:** in every run the parent's first-parent history was
  exactly the engine's merge commits, one per merged ticket, and each merged
  ticket's tested head was in the final parent. Checked again for S1 run 1 from
  a fresh clone of the evidence tag.
- **C2 nothing untested lands:** every merge commit's parents were the revision
  recorded at that ticket's last catch-up and its last tested head.
- **C3:** every refusal was followed by catch-up, then a re-test when the
  catch-up changed the branch, then a merge. No ticket exceeded 3 merge
  executions.
- **C4:** S1 all merged; S2 every ticket ended `merged` or
  `attempts_exhausted`; S3 exactly one ticket exhausted after 3 executions, and
  its file is absent from the final parent.

### 5.2 Negative controls

| Control | Target | Result |
|---|---|---|
| N1 forced pushes, forced collisions | C1 | **Did not fail.** GitHub refused 3 simultaneous forced pushes with `cannot lock ref ... is at <new> but expected <old>`. Every push carries the old value the client read when the push started, and the server checks it. Reproduced by hand on a throwaway branch |
| N1b forced pushes, natural timing (amendment) | C1 | Fail. 5 tickets reported merged; only 2 merges are in the parent; tickets 0, 1 and 4 were overwritten by pushes that started after the parent had moved |
| N2 no re-test after a changing catch-up | C2 | Fail (C3 too): merges landed heads that were never tested |

N1 and N1b show where the protection comes from. Git's push protocol already
refuses two pushes racing from the same starting point. The expected-revision
push (`--force-with-lease` here; `expected_target_revision` in `docs/06`
section 3.3) is what refuses a push based on a catch-up that has since gone
stale. Without it, merges are lost.

### 5.3 The allowance under natural timing

In S2, five tickets with re-tests of 2 to 8 s, two tickets used all 3 merge
executions and ended `attempts_exhausted` in each of the three runs. Each of
their refusals meant a sibling had merged; nothing was wrong with their work.
Under `docs/04` section 7.3 each would escalate to a person. The lock in Spike
3 serialised merges, so waiting cost no attempts; without the lock, waiting is
paid for with attempts.

Real re-tests take minutes, not seconds, which widens the window in which a
sibling can merge, so the effect is likely to be at least as large in real
epics. S1 and S3 show the arithmetic: with forced collisions, the k-th ticket
to merge needs k executions, so any epic with more concurrent tickets than the
allowance can escalate a ticket for no reason.

**Recommendation (LOG 61, unreviewed):** a `base_moved` re-entry does not use
the merge step's attempt allowance. It counts against a separate race allowance,
`limits.races` (default 10), checked on every execution like any allowance, so
floor F4 still bounds the loop. A `base_moved` refusal shows that the run as a
whole is making progress, because a sibling merged, so the loop cannot spin
without something landing. The alternative is to keep one allowance and raise
the merge default to 10, which would also allow ten real merge failures.

### 5.4 What changes in the design

| Finding | Change | Where |
|---|---|---|
| No lost merge, nothing untested, loops terminate | R10 (concurrent work conflicts) mitigated for the lock-free form | `docs/10` section 2 |
| The expected-revision push is what protects a stale catch-up (N1, N1b) | Recorded as evidence for `docs/06` section 3.3 | `docs/10` section 3 |
| The merge allowance escalates tickets that only lost races | Proposed `limits.races` | `docs/04` section 7.3; LOG 61, unreviewed |
| GitHub resets SSH connections under bursts of new ones | Engine-side VCS operations reuse connections and retry connection failures as infrastructure | `docs/10` section 1.2, DR18 |

### 5.5 Limits of the evidence

- Simulated tests of 1 to 8 s, not real test runs.
- Tickets touch distinct files; conflicts were evidenced in Spike 3.
- Pushes used an SSH key, not the engine's GitHub App identity, and the branches
  had no protection rules.
- Harness defects found and fixed during the runs: the first S1 attempt failed
  on a GitHub SSH connection reset, which led to connection reuse, a retry for
  connection errors, and the merge idempotency check; the SSH control socket
  path was too long; the verifier crashed on an overwritten (unreachable) merge
  commit in the first two N1b attempts. Branches left by crashed attempts were
  deleted. No main run needed an infrastructure retry once the fixes were in.

## 6. Open questions

1. LOG 61: should `base_moved` re-entries count against a separate race
   allowance (`limits.races`, default 10) instead of the merge step's attempt
   allowance?
