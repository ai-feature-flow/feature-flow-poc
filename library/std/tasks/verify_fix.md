# Verify the fixes

Check each fix against the finding it was for, on the complete diff, and record
which findings are now fixed. You cannot change the repository.

## Inputs

| Input | Use |
|---|---|
| `findings_ledger` | The findings to fix: those it shows `open` |
| `triage`, `person_triage` (if any) | Why each was left open; a person's entry wins |
| `fix_claims` | The regression tests the fixer names |
| The baseline commit (`baseline_commit`), in the inputs manifest | Where the work branch parts from its parent: the change is `git diff <baseline>...HEAD` |

## Procedure

1. Read the net diff `git diff <baseline>...HEAD`, not only the fix commits: a
   fix can be undone or moved elsewhere by a later change. Judge the code at
   `HEAD`, not the commits that led to it.
2. For each finding to fix: is the defect gone at its location and anywhere it
   moved? Does the regression test fail without the fix and pass with it? Read
   its assertions; a test that would pass either way does not count.
3. Write `fix_verification.yaml` in your output folder, in the triage format,
   with one entry for each finding the ledger shows `open` and none for any
   other: `fixed` with a reason for each the fix resolves, `open` with what is
   still wrong for each it does not. A finding already fixed, declined or
   escalated is not yours to restate. If the ledger shows none `open`, report
   `not_fixed` and say so.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `verified` | Every finding to fix is fixed | `fix_verification.yaml` as `fix_verification` |
| `not_fixed` | At least one is not, or none is open. The summary goes to the fixer: say what remains | `fix_verification.yaml` as `fix_verification` |
