# Approve the {{ run.level }}'s plan for its children

The planner has divided this {{ run.level }} into child units of work and
assigned each the requirements or criteria it owns. The assignments passed their
validators. Approving ratifies the work list and the child requirements at these
exact revisions; each child run starts with them as its authority.

Check:

- Each unit is cohesive and can be reviewed on its own, with a clear boundary.
- Every requirement or criterion has exactly one owner, and every unit owns at
  least one.
- Dependencies between units are stated, so ordering is right.
- Repositories are right for each unit.
- Where this is a revision, each document's `changes` lists what changed since
  the last plan and why. Units whose item is unchanged keep their result; a
  changed or new item runs as a new child, and a unit no longer listed does not
  run. Changing a completed unit's item runs it again on its merged work;
  dropping one does not undo it.

| Answer | Effect |
|---|---|
| `approved` | The child runs start |
| `rejected` | The plan goes back to the planner with your reason |
