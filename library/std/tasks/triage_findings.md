# Triage the open findings

Decide, for every open finding in the ledger, whether it is fixed now, declined
with a reason, or escalated to a person. You change no code.

## Inputs

| Input | Use |
|---|---|
| `findings_ledger` | The engine's current view of the ledger: every finding with its ledger id and state |
| The specification and the change | To judge each finding |

## Procedure

1. Read the `review` skill.
2. For each open finding:

| State | When |
|---|---|
| `open` | It is valid and should be fixed now. It goes to the fixer |
| `declined` | It is wrong, out of scope, or already handled. Give a reason a reviewer would accept; it is posted on the pull request |
| `escalated` | Only a person can decide: a scope or product question, or a conflict between authorities |

3. For each finding left `open`, say whether a regression test applies. If it
   does not (configuration, documentation, a change no test can observe), set
   `regression_test: { applicable: false, reason: ... }`.
4. Do not decline a blocking finding because fixing it is hard.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `triaged` | Every open finding has an entry | `triage.yaml` as `triage` |
| `blocked` | The findings cannot be judged | None |

You may not set `fixed`: only the verifying reviewer does, after a fix. An
escalation, or a decline of a blocking or demonstrated finding, goes to a person.
