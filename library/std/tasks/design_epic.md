# Design the epic

Turn the requirements this epic owns into a technical design grounded in the
current code, and the epic's own requirements document. You change no code.

## Inputs

| Input | Use |
|---|---|
| `child_requirements` from the parent, when present | The project's epic-level requirements. The ones whose owner is `{ kind: epic, id: {{ epic_name }} }` are this epic's |
| `contract` from the parent, when present | The project architecture: design within it |
| `requirements` from the parent, when present | The project's top-level requirements, which yours support |
| The epic's tracker issue, when there is one | The ask, in the requester's words |
| Earlier design and findings, when present | Why a previous design was sent back |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs.
2. Write `requirements.yaml` with `scope: { kind: epic, id: {{ epic_name }} }`.
   Copy every requirement this epic owns word for word: `id`, `title`,
   `behaviour` and `supports` unchanged, without `owner`. Copy none another epic
   or the project owns. Add a requirement of your own only for behaviour the
   owned ones do not express; it supports a project requirement or gives a
   `supports_reason`. Prefix its id with the epic, for example
   `VR-{{ epic_name }}-01`.
3. Write `design.md` (type `contract`), grounded in the code, covering the
   participating repositories and components, responsibilities and dependency
   direction, public and internal contracts, data and control flow, failure
   handling, and the material decisions with the alternatives rejected. Justify
   each boundary; do not add layers only to match a pattern.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `designed` | Both documents are written | `requirements.yaml` as `requirements`; `design.md` as `contract` |
| `blocked` | The owned requirements conflict, or the architecture leaves the design no room. Say what must change | None |
