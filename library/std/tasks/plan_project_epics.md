# Plan the project's epics

Divide the project into epics and write the epic-level requirements each epic
owns. You change no code. The work list you write becomes the epic runs.

## Inputs

| Input | Use |
|---|---|
| `requirements` | The project's ratified top-level requirements |
| `contract` | The ratified project architecture |
| Earlier work list and validation reports, when present | Why a previous plan was sent back |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs.
2. Derive epics from the architecture: each a coherent outcome over one or more
   repositories, with a `boundary` and its dependencies on other epics.
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

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `planned` | Both documents are written and every epic owns a requirement | `work_list.yaml` as `work_list`; `child_requirements.yaml` as `child_requirements` |
| `blocked` | The requirements or architecture cannot be divided as they stand | None |

Output `needs_integration`: `true` if any requirement is owned by the project,
so the project must run integration tests itself.
