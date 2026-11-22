# Triage the open findings

Decide, for every open finding in the ledger, whether it is fixed now, declined
with a reason, or escalated to a person. You change no code.

## Inputs

| Input | Use |
|---|---|
| `findings_ledger` | The engine's current view of the ledger: every finding with its ledger id and state |
| `test_results`, when present | The engine's own latest test run of the change |
| The specification and the change | To judge each finding |

## Procedure

1. Read the `review` skill.
2. For each open finding:

| State | When |
|---|---|
| `open` | It is valid and should be fixed now. It goes to the fixer |
| `declined` | It is wrong, out of scope, or already handled. Give a reason a reviewer would accept; it is posted on the pull request |
| `escalated` | Only a person can decide: a scope or product question, or a conflict between authorities |

3. Judge each finding against the code at `HEAD` (the net diff
   `git diff <baseline>...HEAD`, the baseline commit being in the inputs
   manifest). Decline:
   - a finding about the commit history rather than the code at `HEAD`: an
     intermediate commit a later one corrected, a commit message, the order of
     commits;
   - a finding that repeats another in the ledger, from this review or an
     earlier one, naming the other's ledger id in the reason;
   - a finding marked `demonstrated` that is wrong. It is demonstrated only by
     a command and its output, and not if `test_results` contradicts it;
     reasoning alone is not a demonstration. A person confirms the decline.
4. For each finding left `open`, say whether a regression test applies. If it
   does not (configuration, documentation, a change no test can observe), set
   `regression_test: { applicable: false, reason: ... }`.
5. Do not decline a blocking finding because fixing it is hard.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `triaged` | Every open finding has an entry | `triage.yaml` in your output folder as `triage` |
| `blocked` | The findings cannot be judged | None |

You may not set `fixed`: only the verifying reviewer does, after a fix. An
escalation, or a decline of a blocking or demonstrated finding, goes to a person.
