# Fix the findings

Fix every finding the ledger view shows `open`, each with a regression test
unless the triage says one does not apply. Where a person's triage names a
finding, it decides over the triager's. If the engine's re-test failed or your
regression tests were not accepted, make them pass: they are what sent you
back, and with nothing open they are all there is to fix.

## Inputs

| Input | Use |
|---|---|
| `findings_ledger` | The findings, with their locations and required corrections |
| `triage`, `person_triage` (if any) | Why each finding is open, and where a regression test does not apply; the person's entry wins |
| `implementation_claims` | The current claims, to keep true |
| `fix_verification`, when present | The verifying reviewer's verdict on an earlier fix: what is still wrong |
| `fix_test_results`, when present | The engine's re-test after an earlier fix. If it shows failures, make them pass |
| `fix_validation`, when present | Why the engine did not accept the regression tests in your claims |

## Procedure

1. For each finding to fix, first write a test that fails because of the defect,
   where a regression test applies. Then fix the code until it passes.
2. Keep the change to what the findings need. Do not rewrite unrelated code.
3. Run the test tiers {{ tier_tags }} locally, where they can run in your sandbox
   (a tier with `targets: [ci]` runs only in CI; do not install a toolchain).
4. Write `fix_claims.yaml` in your output folder, in the claims format: for each
   fixed finding's criterion (or the criterion closest to it), the regression
   tests, classified `new` or `modified` against the baseline, each with its
   `source` (`file` and `symbol`). The engine checks that each finding's
   repository has a claimed test whose file is new or changed since the finding
   was raised.
5. Write the implementation claims again as `claims.yaml` in your output folder,
   updated if your fixes changed which tests prove a criterion, unchanged
   otherwise.
6. Commit your changes to the code; the checkout must be clean. A fix that
   commits nothing is sent to a person.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `fixed` | Every finding to fix is fixed and committed | `fix_claims.yaml` as `fix_claims`; `claims.yaml` as `implementation_claims`, both in your output folder |
| `blocked` | A finding cannot be fixed within the ratified plan. Say which and why | None |
