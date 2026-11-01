# SP4: lock-free merging under forced races

Status: plan and checks pre-registered 2026-11-01, before any run. Results
follow in section 5 once run.

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

Not run yet.

## 6. Open questions

None yet.
