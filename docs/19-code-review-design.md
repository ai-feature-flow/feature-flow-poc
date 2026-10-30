# Code review design

Written 2026-10-31. Decided by the product owner the same day, as the direction
for version 1.

## Why this note exists

The workflow is meant to produce well designed, well structured, reliable,
bug-free code without a human until the final merge. Review is the main thing
standing between an agent's work and that goal, so it has to catch real
problems without flooding the fix loop with noise.

The spikes did not design review. Their reviewer was a measuring instrument: it
saw only a diff, with no repository, so the Spike 3 measurement could compare
ticket-level and epic-level reviews fairly. That was right for the spike and
wrong for the product. Spike 3 also showed the danger of a reviewer that sees
only part of a change: the engine cut the diff to 6000 characters, and the
reviewer never saw the code with the bug in it (`docs/18`, the Spike 3
results).

## The design

1. **Machines check first.** Before any AI review, the engine runs the build,
   tests, linters, analysers and formatting, and measures test coverage of the
   changed lines. The reviewer gets these results instead of rediscovering
   them.

2. **The reviewer is an agent on a read-only checkout of the branch.** It is
   told which branch to review and against which base, and given the list of
   changed files. It reads the diff itself (`git diff`), opens any other file it
   needs (callers, contracts, tests), and may build and run tests. It cannot
   change the branch. Nothing is pre-loaded into its prompt beyond the brief.

3. **It reviews against a specification,** not just for general quality:
   - the ticket's acceptance criteria;
   - the epic's contract;
   - the repository's own conventions and architecture notes.

4. **Separate review angles,** each a focused pass:
   - correctness (bugs, edge cases);
   - design and structure (fits the existing architecture, simplest thing
     that works);
   - tests (would these tests fail if the code were wrong?);
   - security;
   - conformance to the specification.

   A single "review everything" pass covers each thinly, and is part of why
   Spike 2 saw different findings on each run of the same change.

5. **Bug findings carry evidence.** Where it can, the reviewer writes a
   throwaway test that fails because of the bug. A demonstrated bug is high
   confidence and blocks the merge. A purely reasoned concern is labelled as
   reasoned and ranked lower. Demonstrated findings do not flip-flop between
   runs, so this is also the main defence against noisy reviews.

6. **A different, ideally stronger, model reviews,** for example Sonnet or Opus
   reviewing work authored on Haiku. A model is poor at spotting its own blind
   spots.

7. **The engine checks coverage.** Every file the reviewer opens is recorded
   through the agent runtime's pre-tool-call hook, which fired reliably in
   Spikes 2 and 3. A review that never opened a changed file is incomplete and
   is re-run, not accepted.

8. **Two review levels, both with repository access:**
   - **Ticket review:** the ticket branch against the integration branch,
     given the ticket and the epic's contract. Spike 3's pilot showed that a
     ticket review given the epic contract catches a ticket that departs from
     it (`docs/17` section 6.6, the review pilot).
   - **Epic review:** the whole integration branch against `main`, looking for
     problems that only exist once every ticket is combined.

9. **What stays as already decided:**
   - **The findings ledger** is the record of every finding and its status,
     and is the source of truth over any single review pass. A finding marked
     fixed stays fixed while the code it was about is unchanged. There is no
     voting between passes (`docs/17` section 6.5, accepted 2026-10-30).
   - **The fix loop** requires a regression test for every fixed finding.
   - **A human approves** any merge into the default branch (floor F1, which
     no workflow may relax).

## What changes elsewhere

| Area | Change |
|---|---|
| Size limit | No diff in the prompt, so no prompt limit. The engine's coverage check replaces it. The visible `diff_too_large` failure added in Spike 3 stays only for the spike's diff-only reviewer |
| Pre-submit check (a developer running the gated review from their own session) | "Identical input" no longer proves parity, because the reviewer explores. Parity becomes: same brief, same tools, same model, same commit |
| Cost | More tokens per review: reading files, separate angles, a stronger model. Accepted; cheap relative to a defect reaching `main` |
| Run-to-run variation | Exploration varies between runs. The findings ledger and evidence-backed bug findings are what absorb it |

## Not decided here

- Which angles run at ticket level and which only at epic level.
- Whether a reviewer may run the full test suite, or only targeted tests, given
  sandbox time.
- How a reasoned (undemonstrated) finding is triaged: blocking, advisory, or
  routed to a human.
