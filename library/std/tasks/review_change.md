# Review the change: {{ angle }}

Review this run's change from the **{{ angle }}** angle and report findings. You
cannot change the repository. You review independently: you are not given the
run's history, only the change, the specification, the findings already raised
and your inputs.

## Inputs

| Input | Use |
|---|---|
| The specification: `requirements`, and `delivery_plan` or `contract`, staged in your inputs (the run's versions of {{ spec_refs }}) | What the change must do |
| `implementation_claims`, when present | The tests the implementer says prove each criterion |
| `findings_ledger` | The findings already raised in this run, with their ledger ids and states; empty on the first review |
| The baseline commit (`baseline_commit`), in the inputs manifest | Where this run's work branch parts from its parent: the change is `git diff <baseline>...HEAD` |

## Procedure

1. Read the `review` skill.
2. Review the change as it stands at `HEAD`: the net diff
   `git diff <baseline>...HEAD`. Then open every changed file with the `Read`
   tool. The engine records the files you open with `Read`; a file shown
   through a shell command (`cat`, `git show`, the diff) does not count, and a
   review that left changed files unread is sent back.
3. Judge the code at `HEAD`, not how it got there. An intermediate commit that a
   later one corrected, a commit message or the order of commits is not a
   finding, unless the ticket asks for work on the history. `git log` is context
   only.
4. If the baseline commit cannot be resolved, report `blocked`. Do not
   substitute `HEAD~1` or a guessed merge base.
5. Judge the change from your angle:

| Angle | Ask |
|---|---|
| `correctness` | Does it do what the specification says, including edge cases and error paths? |
| `design` | Does it fit the design and the repository's structure? Is it maintainable? |
| `tests` | Do the claimed tests' setup and assertions show each criterion's behaviour? Are removed or weakened tests declared and replaced? Are the `new` and `reused` classifications true? |
| `security` | Input handling, secrets, authorisation, injection, unsafe defaults |
| `specification` | Does every criterion hold, and nothing outside the scope change? Do criteria copied from a parent keep their meaning? |

6. For each finding, give a `rule` from the review skill's vocabulary, its location (repository, file,
   enclosing symbol and the code snippet, never line numbers), the defect as
   `title` and `description`, and the `required_correction`. Write the report
   exactly in the format the `review` skill shows.
7. Check each finding against the ledger. If the ledger already holds the same
   problem at the same place (the same repository and file), in any state, set
   `same_as: <ledger id>` on your finding; a finding in another file is a new
   one, without `same_as`. Do not
   raise a finding the ledger shows `declined` again unless the code it concerns
   has changed since; if it has, say in the description what changed.

## What blocks

Set `blocking: true` only for a finding with `basis: demonstrated`: you ran a
command, such as a failing test you wrote, that shows the defect, and its output
is the proof. Put the command and its output in `proof`. A finding you reason
about but did not show by running something is `basis: reasoned` and
`blocking: false`, however serious its severity. A style preference is not a
finding.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `reviewed` | The review is complete, with or without findings | `findings.yaml` in your output folder as `findings_report`, `angle: {{ angle }}` |
| `blocked` | The review cannot be done: the baseline is missing, or the specification conflicts with itself | None |
