# Merge design

Written 2026-10-31. Decided by the product owner the same day, in two rounds:
the flow without a lock, then the merge parameters, branch levels and the
default-branch setting.

## Branch levels

Work flows up through three levels of branch, then into `main`:

| Merge | Squash? | Who merges | Why |
|---|---|---|---|
| Ticket branch into epic branch | Yes | The engine | One commit per ticket is enough; nobody needs every work-in-progress commit |
| Epic branch into project branch | No | The engine | A plain merge keeps each ticket's commit visible |
| Project branch into `main` | No | A human, by default | See "Merging into the default branch" below |

A project run starts epic runs the way an epic run starts ticket runs.

## The merge step's parameters

| Parameter | Meaning |
|---|---|
| `engine_can_merge` | `true`: the engine performs the merge. `false`: the engine raises an intervention (a request for a human), waits, a human merges on the code host, and the engine detects the merge and carries on |
| `squash` | `true` for a squash merge, `false` for a plain merge |

## The flow, where the engine merges

Tickets (and epics) merge the way a team of developers does, with no lock:

1. The work is built and tested on its own branch.
2. **Catch up:** the engine merges the latest target branch into the work
   branch, and re-runs the tests if that changed anything.
3. **Merge:** the engine builds the merge commit (or the squash commit) from
   exactly what was tested, and pushes it to the target branch. Git refuses a
   push onto a branch that has moved since it was fetched. If that happens,
   the step returns `base_moved` and the work goes back to step 2.

Nothing untested can land: a merge only succeeds if the target is exactly what
the work was last tested against.

The number of tries is the merge step's attempt allowance, default 3, counting
every attempt. Like every limit, it is set by an engine default and can be
overridden by the workflow, a child workflow, or the step. When it runs out,
the work escalates to a human.

A squash-merged ticket's pull request does not show as "merged" on GitHub,
because its own commits never reach the target. The engine closes it with a
comment linking the squash commit; the pull request stays as the review record.

## Merging into the default branch

**Default: the engine never merges into the default branch** (usually `main`).
The plan sets `engine_can_merge: false` there, a human merges, and the engine
waits for it.

This is a setting, not a fixed rule, because some repositories (a proof of
concept, say) may be happy for the engine to merge into `main`:

- It is set hierarchically: organisation, then repository, then workflow.
- **Nothing an agent can edit can change it.** The repository's value is read
  from its configuration on its default branch, never from a ticket branch, and
  a plan change made during a run cannot change it. The organisation can lock
  it so lower levels cannot relax it.
- The engine enforces it twice: when the plan is validated, and again at merge
  time, reading the default branch from the code host rather than from any
  file.
- **The final fallback is the code host.** Branch protection on `main` requires
  an independent review, and the engine never holds a way to bypass it.

This replaces safety floor F1, which said a human must approve every merge into
the default branch and no workflow may relax it. The default is the same; it
can now be relaxed deliberately, by someone who is not an agent.

## Who can push

- **Agents never hold a credential that can push.** An agent commits in its own
  sandbox. As part of the agent step, the engine fetches those commits and
  pushes them to the ticket branch with its own credential, at the end of the
  step and at work-in-progress points. GitHub cannot limit a token to
  particular branches, so withholding the credential is the reliable control.
- A step whose role has no write access pushes nothing, and fails if it made
  commits anyway. A `push` parameter (default `true`) lets a workflow switch
  pushing off for scratch steps. A failed step's commits go to a hidden
  work-in-progress reference, not the ticket branch, so a retry can pick them
  up.
- Agents get read-only access: the run's repository, plus any other
  repositories the workflow declares, for example to check a contract.
- The engine performs every engine merge as a workflow step. Workflow steps are
  durable: after a crash they resume and complete exactly once, which Spike 1
  proved (`docs/15`).
- A human's interactive session may get a "merge when ready" tool, but it only
  asks the engine to run the merge step.

## What this replaces

Spike 3 built and tested a merge lock: one ticket at a time held it across
catch-up, re-test and merge (`docs/17` section 4.3). It worked, but a ticket
that was still open yet stuck held the lock and blocked every other merge
(`docs/18` section 6, requirement D3). The flow above removes the lock and that
failure mode with it.

What the lock bought that this does not: a ticket never re-runs its tests
because it lost a race. With two or three tickets running at once, races are
rare and a re-run costs one CI run. If contention ever becomes real, a code
host merge queue is the managed answer.

The parts Spike 3 proved still apply: the catch-up step reports whether the
branch changed, tests re-run only when it did, and a merge conflict goes to an
agent to resolve, not to a human.
