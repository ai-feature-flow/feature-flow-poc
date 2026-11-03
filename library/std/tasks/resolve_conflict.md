# Resolve the merge conflict

The parent branch moved, and merging it into this run's work branch conflicts.
Merge it and resolve the conflict so that both sides keep their intent.

## Procedure

1. Merge the parent branch into the current branch: the inputs manifest names
   it. Do not rebase: published history stays as it is.
2. For each conflict, read both sides and the commits that made them. Keep the
   parent's change and this run's change; where they truly contradict, prefer
   the parent's behaviour and adapt this run's work to it.
3. Build, and run the fastest test tier the manifest declares that can run in
   your sandbox (a tier with `targets: [ci]` runs only in CI; do not install a
   toolchain).
4. Commit the merge. The checkout must be clean.

The run re-tests and re-reviews the result after this step.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `resolved` | The merge is committed and the fast tier passes | None |
| `blocked` | The two sides cannot both hold, so a person must choose. Say which files and what conflicts | None |
