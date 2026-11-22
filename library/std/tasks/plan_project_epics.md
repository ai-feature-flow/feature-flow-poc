# Plan the project's epics

Divide the project into epics and write the epic-level requirements each epic
owns. You change no code. The work list you write becomes the epic runs.

## Inputs

| Input | Use |
|---|---|
| `requirements` | The project's ratified top-level requirements |
| `contract` | The ratified project architecture |
| Earlier work list and the last validation report, when present | The last plan's validation; on rework after `invalid`, why the plan failed |
| `previous/work_list` and `previous/child_requirements`, when present | Your last plan: this execution revises it |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs.
2. Derive epics from the architecture: each a coherent outcome over one or more
   repositories. Give each a short stable `name` (snake_case: it becomes the
   epic's id and branch), a `title`, its `repositories`, a concrete `boundary`,
   and its dependencies on other epics (`depends_on`, by name).
3. Write `child_requirements.yaml` (a requirements document) with
   `scope: { kind: project, id: <the project's scope id> }`. For every project
   requirement write one or more epic-level requirements supporting it. Give
   each exactly one `owner`: the epic that delivers it, or the project itself
   when only the project's integration run, across repositories, can prove it.
   A project-owned requirement needs an epic that writes its integration tests;
   say which in that epic's `boundary`.
4. **Every epic must own at least one requirement.**
5. Write `work_list.yaml` with `level: epic` and one item per epic, listing in
   `owns` the requirements it owns.
6. **Revising a plan.** When `previous/...` inputs are present, start from them.
   Keep what still stands: epic names, requirement ids and completed epics'
   boundaries. Change only what the problem requires (a failed epic, the
   validation report or a person's reason in the run context) and what depends
   on it, adding epics or requirements as needed. An epic whose item is
   unchanged is not run again; a changed or new item runs as a new epic.
   Changing a completed epic's item runs it again on its merged work; dropping
   one does not undo it. Never
   drop a requirement only to get past the problem. List in each document's
   `changes` what changed and why; leave `changes` out of a first plan.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `planned` | Both documents are written and every epic owns a requirement | `work_list.yaml` as `work_list`; `child_requirements.yaml` as `child_requirements` |
| `blocked` | The requirements or architecture cannot be divided as they stand | None |

Output `needs_integration`: `true` if any requirement is owned by the project,
so the project must run integration tests itself.
