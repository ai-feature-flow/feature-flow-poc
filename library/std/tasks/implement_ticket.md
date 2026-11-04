# Implement the ticket and claim its evidence

Implement **{{ ticket_id }}** against its ratified requirements and acceptance
criteria, and say which tests prove each criterion. Do not change the ratified
plan.

## Inputs

| Input | Use |
|---|---|
| `requirements`, `delivery_plan` | The ratified plan. Your authority |
| `contract` from the parent, when present | The epic's design: stay within its boundary |
| `baseline_test_results` | The tests that existed before this run, for classifying claims |
| Earlier `implementation_claims`, test results, validation reports and the run context, when present | What a previous attempt did and why it was sent back |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs.
2. Read the ticket (`/ff/inputs/ticket.md`, read from the tracker when this step
   started, when the run has one) and compare any new clarification with the ratified plan. A
   material conflict is `blocked`, not an unapproved change of scope. If a
   person's answer would settle it, ask with `ask_human`.
3. Start from the current checkout. On rework, earlier commits are normally
   kept, but a person may have reset the branch; work from what is there.
4. Implement every criterion within the design's boundary, following the
   repository's conventions. Add commits; do not rewrite history.
5. Run the repository's declared test tiers that can run in your sandbox. Read
   the parsed result files and copy each claimed test's identity exactly as it
   appears there. A tier whose manifest entry has `targets: [ci]` runs only in
   CI, after you report: do not install a toolchain to run it. Name its tests
   as the test framework reports them (for xUnit, the fully qualified method
   name), and leave them to the engine's run.
6. Write `claims.yaml`: for every criterion and method, the tests that prove it,
   at the method's repository and tier, each classified against the baseline as
   `new`, `reused` or `modified`, and only as the method allows. List every
   baseline test you removed under `test_impacts`, with its replacement.
7. Commit everything. Check that `git status --porcelain` prints nothing.

The engine runs the tests itself at the frozen commit and checks your claims
against what it observed. Your local run does not replace that.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `implemented` | Every criterion is implemented and claimed, and the checkout is clean | `claims.yaml` as `implementation_claims` |
| `blocked` | The ratified plan is wrong or conflicts with the ticket, required evidence is impossible, or a tool is unavailable. Say what is needed | None |
