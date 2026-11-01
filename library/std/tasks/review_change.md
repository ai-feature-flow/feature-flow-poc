# Review the change: {{ angle }}

Review this run's change from the **{{ angle }}** angle and report findings. You
cannot change the repository. You review independently: you are not given the
run's history, only the change, the specification and your inputs.

## Inputs

| Input | Use |
|---|---|
| The specification: {{ spec_refs }} | What the change must do |
| `implementation_claims`, when present | The tests the implementer says prove each criterion |
| The baseline commit, in the inputs manifest | Where this run started |

## Procedure

1. Read the `review` skill.
2. Review every commit and the complete diff from the baseline commit to `HEAD`:
   `git log --stat <baseline>..HEAD` and `git diff <baseline> HEAD`. Open every
   changed file. The engine records which files you opened, and a review that
   left changed files unread is sent back.
3. If the baseline commit cannot be resolved, report `blocked`. Do not
   substitute `HEAD~1` or a guessed merge base.
4. Judge the change from your angle:

| Angle | Ask |
|---|---|
| `correctness` | Does it do what the specification says, including edge cases and error paths? |
| `design` | Does it fit the design and the repository's structure? Is it maintainable? |
| `tests` | Do the claimed tests' setup and assertions show each criterion's behaviour? Are removed or weakened tests declared and replaced? Are the `new` and `reused` classifications true? |
| `security` | Input handling, secrets, authorisation, injection, unsafe defaults |
| `specification` | Does every criterion hold, and nothing outside the scope change? Do criteria copied from a parent keep their meaning? |

5. For each finding, give a stable `rule`, its location (file, enclosing symbol
   and the code snippet, never line numbers), the defect, and the correction
   required.

## What blocks

Set `blocking: true` only for a finding with `basis: demonstrated`: you wrote a
failing test, ran a command that shows the defect, or can point to a criterion
the change plainly does not meet. Put the proof in `proof`. A finding you
reason about but cannot demonstrate is `basis: reasoned` and `blocking: false`,
however serious its severity. A style preference is not a finding.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `reviewed` | The review is complete, with or without findings | `findings.yaml` as `findings_report`, `angle: {{ angle }}` |
| `blocked` | The review cannot be done: the baseline is missing, or the specification conflicts with itself | None |
