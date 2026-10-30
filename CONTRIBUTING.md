# Contributing

How work on this design repository is done. Written 2026-10-31 from the Spike 3
handover, which it replaces.

## Where things are

| What | Where |
|---|---|
| Design documents | `docs/01` to `docs/14`, plus design notes from `docs/19` on |
| Spike plans and results | `docs/08` section 2 (the spike plan), `docs/15` (Spike 1), `docs/16` (Spike 2 and the Claude Code proof), `docs/17` and `docs/18` (Spike 3) |
| Reviews from the product owner | `docs/reviews/review-NN.md` |
| Spike code | `spikes/01-interpreter`, `spikes/02-real-run`, `spikes/03-concurrent` |
| Fixture repository | `ai-feature-flow/feature-flow-example`, a small .NET API the spikes run against |

## Writing

- **Keep documents short**, and write for a reader who has not just read
  everything else.
- **Explain every reference where it is used.** Not "E3 is met (R10, section
  1.3)", but "E3, no ticket blocked by another's failure, is met". The reader
  should never have to jump to another section or document to follow a
  sentence.
- **End each deliverable with an open questions section.** The product owner
  answers in the next review. When all are answered, say "None open" and keep
  the answers.
- **Flag judgement calls.** Anything decided without being asked is listed as
  such, so it can be reviewed rather than looking settled.
- **Check cross-references after editing.** A stale section number is worse
  than none.

## The review cycle

Work is pushed to `main`. The product owner replies with a review file in
`docs/reviews/`. Apply it, then push. Keep design work on `main`; do not drift
onto a branch.

## Commits

- Stage files explicitly. Never `git add -A`: it has swept up scratch files
  before.
- Say in the message what was verified and how.
- End every message with:
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

## Verify, do not assert

This is the convention that has caught the most mistakes.

- **A check that passes by construction proves nothing.** Before trusting a
  result, ask what would make it come out differently. If nothing could, the
  check is circular.
- **Check the method on an obvious case first.** For example, run the merge
  check with the lock bypassed and confirm it does see overlaps.
- **Never substitute canned content for the thing under test.** If a
  credential is missing, stop and ask. Stopping to ask is the agreed working
  model, not a failure.
- **Verify what helper agents claim.** Check the decisive evidence yourself (the
  commit on the remote, the session transcript, the record) before repeating it.
- **Fix the method, then pre-register.** If a measurement turns out flawed, fix
  it, write down the thresholds before re-running, and keep the flawed result as
  history (Spike 3 did this for its review measurement).

## Spikes

Spikes prove a design concept is feasible. A spike review asks only two
questions: does the evidence support the feasibility claim, and did the spike
teach something that changes the design? Production concerns found along the
way are recorded as design requirements (`docs/08` section 1.3), not fixed in
spike code. Keep spike code as cheap as the evidence allows.

## Working with helper agents

One agent coordinates and integrates; helper sessions each own one piece of
work. Give each helper its own Temporal port, task queue and directory where it
touches shared infrastructure: in Spike 1, two agents sharing a worker corrupted
a run.

## Local setup (macOS)

| Need | Detail |
|---|---|
| .NET | SDK 10. All spike code targets `net10.0`. The fixture targets `net9.0`; run its tests with `DOTNET_ROLL_FORWARD=Major` |
| Temporal | CLI at `~/.temporalio/bin/temporal`. Dev server ports used so far: 7233 (Spike 1), 7333 (Spike 2), 7533 (Spike 3) |
| Agent runtime | `claude` on `PATH`, signed in to the Anthropic subscription. The spikes use model `haiku` |
| Secrets | `~/keys/feature-flow-secrets.json`, outside the repository, passed as `FF_SECRETS_FILE`. It holds the GitHub App id (5507595) and installation id (176108462), and the path to the App's private key. Never commit a credential, and never print one |
| Pushing | `origin` uses an SSH host alias that can push to both the design and the fixture repository |

## Design principles worth not losing

Each has bitten the project or its prototype at least once.

- **An interruption must become a continuation, never a wrong outcome.** A run
  that ends without reaching an `end:` route must not report `completed`
  (`docs/08` section 1.3, requirement R6).
- **Infrastructure failures never use up a ticket's attempts.** A long outage
  stalls and alerts; it never fails on its own.
- **Events wake, reads confirm.** An event is a hint that something may have
  changed, never the source of truth.
- **Repository facts are read at each step, never copied into the plan.** The
  prototype went stale exactly this way.
- **Privilege comes from the role, never the step.** An agent cannot grant
  itself write access or tools by editing its own plan.
- **A safety rule must not depend on a file the governed party can edit.**
  Spike 2's merge check read the default branch from a file the agent could
  change.
- **Who made a decision comes from the authenticated identity, never the
  request body.** That is what makes "a human must approve merges to `main`"
  enforceable.
- **Agents never hold a credential that can push,** and model credentials stay
  out of the agent's environment (`docs/20`, the merge design).
