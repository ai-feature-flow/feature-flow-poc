# Spike 3 review pilot

Run before any Spike 3 engine work (`docs/17` section 6.6). No engine: the three
tickets were written by hand on a copy of `feature-flow-example` `main`, and the
diffs below were reviewed with `claude -p --model haiku` using Spike 2's
reviewer prompt plus a `message` field.

| File | What |
|---|---|
| `diffs/A.diff`, `diffs/B.diff` | Ticket A and ticket B against `main` |
| `diffs/EPIC.diff` | A, C and B combined: the seeded defect is present |
| `diffs/NEG.diff` | As EPIC, but `FindByName` normalises its input and B's test agrees with the contract |
| `pilot-v1.py`, `pilot-v1-results.json` | First epic wording and rubric. The Neg control failed |
| `pilot-v2.py`, `pilot-v2-results.json` | Epic wording and rubric as in `docs/17` 3.1 and 6.3 |

Re-run (the diffs are read from `diffs/`, results written beside the script):

    python3 pilot-v2.py 10
