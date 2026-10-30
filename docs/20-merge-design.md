# Merge design

Written 2026-10-31. Decided by the product owner the same day.

## The flow

Tickets merge into their epic's integration branch the way a team of developers
does, with no lock:

1. The ticket is built and tested on its own branch.
2. **Catch up:** the engine merges the latest integration branch into the ticket
   branch, and re-runs the tests if that changed anything.
3. **Merge:** the engine merges the ticket branch into the integration branch.
   If the integration branch has moved since step 2 (another ticket merged
   first), the merge is refused and the ticket goes back to step 2.

After about 3 refusals in a row the ticket escalates to a human, so a slow
ticket cannot be starved forever by faster siblings.

Nothing untested can land: a merge only succeeds if the integration branch is
exactly what the ticket was last tested against.

## How a stale merge is refused

The engine performs the merge itself: it creates the merge commit locally from
exactly what was tested, and pushes it to the integration branch. Git rejects a
push onto a branch that has moved since it was fetched, so a stale merge cannot
happen. GitHub marks the pull request merged once its commits reach the base.

This needs no branch protection settings and no extra permissions for the
engine.

## Who can push

- **Agents never hold a credential that can push.** An agent commits in its own
  sandbox; the engine pushes the ticket branch for it at the end of each step
  and at work-in-progress points. GitHub cannot limit a token to particular
  branches, so withholding the credential is the only reliable control. This
  also follows the rule that credentials never enter an agent's environment
  (`docs/08` section 1.3, requirement R1).
- **The engine performs every merge, as a workflow step.** Workflow steps are
  durable: after a crash they resume and complete exactly once, which Spike 1
  proved (`docs/15`). Policy checks (tests passed on this exact code, findings
  resolved, human approval for `main`) live in one place.
- **Backstop on the code host:** rules on `main` and on integration branches
  allow only the engine's identity to update them, so an engine bug is also
  caught by GitHub.
- **A human's interactive session** may get a "merge when ready" tool, but the
  tool only asks the engine to run the merge step. It never merges itself.

## What this replaces

Spike 3 built and tested a merge lock: one ticket at a time held it across
catch-up, re-test and merge (`docs/17` section 4.3). It worked, but a ticket
that was still open yet stuck held the lock and blocked every other merge
(`docs/18`, requirement D3). The flow above removes the lock, the lock
workflow and its liveness checks, and that failure mode with them.

What the lock bought that this does not: a ticket never re-runs its tests
because it lost a race. With two or three tickets running at once, races are
rare and a re-run costs one CI run. If contention ever becomes real, GitHub's
merge queue is the managed answer.

The parts Spike 3 proved still apply unchanged: the catch-up step reports
whether the branch changed, tests re-run only when it did, and a merge conflict
goes to an agent to resolve, not to a human.
