# Repair the failing checks

The engine's own test run, static analysis or claims check failed on this run's
change. Make it pass without weakening what it checks.

## Inputs

| Input | Use |
|---|---|
| `test_results`, when present | The engine's test results: which tests failed, and their output |
| `analysis_results`, when present | New static analysis issues against the baseline |
| `verification_report`, when present | Which claims did not match a passing test at the right tier |
| `implementation_claims`, `delivery_plan` | What is claimed, and what was required |

## Procedure

1. Find the cause of each failure from the evidence above, not from a guess.
2. Fix the code, or correct a claim that named the wrong test or tier. Do not
   delete, skip or weaken a test to make it pass, and do not change the ratified
   plan.
3. Run the affected tiers locally.
4. Produce `claims.yaml` again, corrected where a claim was wrong.
5. Commit everything; the checkout must be clean.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `repaired` | The cause of each failure is fixed and committed | `claims.yaml` as `implementation_claims` |
| `blocked` | A failure cannot be fixed within the plan, or comes from outside the change. Say which | None |
