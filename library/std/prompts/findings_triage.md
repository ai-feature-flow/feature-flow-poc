# Triage the findings an agent may not clear alone

The fix loop stopped for you: its triager escalated a finding, or declined one
that is blocking or demonstrated (with a failing test or other proof). An agent
alone may not clear either. Your triage is applied to the run's findings ledger
and recorded as you.

Read the ledger (`GET /runs/<run id>/findings`, or `ff run findings <run id>`):
every finding with its ledger id, state and the reviewer's description. The
triager's entries, with its reasons, are the `triage` artifact.

Answer `triaged` with an artifact `person_triage` of type `triage`, with an entry
for every finding the ledger holds `escalated`, and every `declined` one that is
blocking or demonstrated; a triage leaving one out comes back to you:

```yaml
schema: 1
entries:
  - { finding: <ledger id>, state: open, reason: "Fix it: ..." }
  - { finding: <ledger id>, state: declined, reason: "Not a defect: ..." }
```

| State | Effect |
|---|---|
| `open` | The fixer fixes it, with a regression test, and a reviewer verifies the fix |
| `declined` | Kept as declined while its code is unchanged; the reason is posted on the pull request when one is open |
| `escalated` | The loop leaves by its `escalated` exit, to whoever handles that |
| `fixed` | Only if something was committed to its repository since it was raised |

When nothing is left open the loop ends; otherwise the fixer runs. A triage naming
a finding the ledger does not hold comes back to you. Answer `escalate` to leave
the loop without triaging.
