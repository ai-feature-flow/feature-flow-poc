# Fix the findings

Fix every finding the triage left `open`, each with a regression test unless the
triage says one does not apply.

## Inputs

| Input | Use |
|---|---|
| `findings_ledger` | The findings, with their locations and required corrections |
| `triage` | Which findings to fix, and where a regression test does not apply |
| `implementation_claims` | The current claims, to keep true |

## Procedure

1. For each finding to fix, first write a test that fails because of the defect,
   where a regression test applies. Then fix the code until it passes.
2. Keep the change to what the findings need. Do not rewrite unrelated code.
3. Run the test tiers {{ tier_tags }} locally.
4. Write `fix_claims.yaml` in the claims format: for each fixed finding's
   criterion (or the criterion closest to it), the regression tests, classified
   `new` or `modified` against the baseline.
5. Produce the implementation claims again, updated if your fixes changed which
   tests prove a criterion, unchanged otherwise.
6. Commit everything; the checkout must be clean.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `fixed` | Every finding to fix is fixed and committed | `fix_claims.yaml` as `fix_claims`; `claims.yaml` as `implementation_claims` |
| `blocked` | A finding cannot be fixed within the ratified plan. Say which and why | None |
