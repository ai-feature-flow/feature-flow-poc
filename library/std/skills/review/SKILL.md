---
name: review
description: How std reviews work - what a finding is, how it is fingerprinted, what blocks a merge, and how findings are triaged and fixed. Read before reviewing, triaging or verifying a fix.
---

# Review

## The ledger

Every finding goes into the run's findings ledger. A review adds to the ledger;
it does not replace it. The engine matches a new finding against the ledger by
its fingerprint, so the same problem raised twice is one finding raised again,
not two.

| Fingerprint part | Write it so it is stable |
|---|---|
| `rule` | The kind of defect, from the vocabulary below; the engine refuses any other |
| `location.file`, `location.symbol` | The file and the enclosing function or class |
| `location.snippet` | The code the finding is about, a few lines, copied exactly |

Never use line numbers: they move.

Rules, one per finding; pick the nearest. Two reviewers raising the same
problem then give it the same rule, so it is matched as one finding.

| Rule | The defect |
|---|---|
| `wrong_behaviour` | The change does not do what a requirement or criterion says, or does something outside its scope |
| `null_dereference` | A value that may be null or missing is used |
| `missing_validation` | Input is not checked before use |
| `missing_authorisation` | An action is not checked against who may take it |
| `injection` | Untrusted input reaches a query, command, path or template |
| `secret_exposed` | A credential or secret is logged, returned, committed or sent |
| `security` | Any other security defect: unsafe defaults, weak crypto, information leaks |
| `error_handling` | Errors are swallowed, mis-reported or leave inconsistent state |
| `resource_leak` | A connection, file, handle or subscription is not released |
| `concurrency` | A race, deadlock or unsafe shared state |
| `performance` | Needless work that matters at the expected scale |
| `api_contract` | A published interface, schema or contract changes incompatibly |
| `missing_test` | Behaviour the change adds or alters has no test |
| `test_asserts_nothing` | A test whose setup or assertions do not show the behaviour it claims |
| `convention` | The repository's stated conventions are not followed |
| `maintainability` | Duplication, needless complexity or misleading names |
| `documentation` | Documentation the change makes wrong or leaves missing |

A finding that was fixed or declined reopens only if its code changed since
(the std setting). Raising it again on unchanged code does not reopen it.

## What blocks

| Basis | Meaning | Blocks a merge |
|---|---|---|
| `demonstrated` | You proved it: a failing test, a command and its output, or a criterion the change plainly does not meet. The proof goes in `proof` | Yes, `blocking: true` |
| `reasoned` | You argue it from reading the code | No, `blocking: false`, whatever the severity |

The merge bar in std is: no open blocking finding. A reasoned finding is
recorded and may be fixed, but it does not hold the change. If you believe a
reasoned finding is serious, demonstrate it.

## Writing a finding

Say what is wrong, where, and what correction is required, specifically enough
for a fixer who has not read your review to act on it. Distinguish a required
correction from a suggestion.

## Triage and fixing

A triager decides per open finding: fix it now (`open`), decline it with a
reason a reviewer would accept, or escalate it to a person. A fix adds a
regression test that fails without the fix, unless the triage records why no
test applies. A verifying reviewer checks each fix on the complete diff and
marks the finding `fixed` only if the defect is gone.

A finding fixed twice and raised again escalates to a person: the loop is not
converging.

## Report formats

The engine reads these files exactly; any other field name, or YAML that does
not parse, stops the step for a person. Write every sentence as a block scalar
(`>-` and the text indented on the next lines), never as plain text after the
colon: a `: ` inside plain text breaks the YAML. A reviewer writes a findings
report (`findings.yaml`):

```yaml
schema: 1
angle: correctness
findings:                       # [] when there are none
  - rule: null_dereference      # a stable id for the kind of defect
    title: Find may dereference a null name
    description: >-
      What is wrong and why, for a fixer who has not read your review.
    required_correction: >-
      What must change.
    severity: high              # critical, high, medium or low
    blocking: false             # true only with basis: demonstrated
    basis: reasoned             # demonstrated or reasoned
    proof: >-
      For demonstrated only: the failing test or command and its output.
    location:
      repository: owner/name    # the repository named in the inputs manifest
      file: src/Example.cs
      symbol: Example.Find
      snippet: return items.First(i => i.Name == name);
```

A triager, and a verifying reviewer in its `fix_verification.yaml`, write a
triage, one entry per finding by its `ledger_id` from the ledger view:

```yaml
schema: 1
entries:
  - finding: <ledger_id>
    state: open                 # open, fixed, declined or escalated, as your task allows
    reason: >-
      Why.
    regression_test: { applicable: true }   # or { applicable: false, reason: ... }
```
