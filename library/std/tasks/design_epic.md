# Design the epic

Turn the requirements this epic owns into a technical design grounded in the
current code, and the epic's own requirements document. You change no code.

## Inputs

| Input | Use |
|---|---|
| `child_requirements` from the parent, when present | The project's epic-level requirements. The ones whose owner is `{ kind: epic, id: {{ epic_name }} }` are this epic's |
| `contract` from the parent, when present | The project architecture: design within it |
| `requirements` from the parent, when present | The project's top-level requirements, which yours support |
| The epic's tracker issue, `/ff/inputs/ticket.md`, when there is one | The ask, in the requester's words |
| Earlier design and findings, when present | Why a previous design was sent back |
| `previous/requirements` and `previous/contract`, when present | Your last design: this execution revises it |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs. If
   there is no `child_requirements` from the parent and the manifest's `ticket`
   is null, stop and report `blocked`: the run has no ticket. Never write
   requirements nobody asked for.
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
4. **Revising a design.** When `previous/...` inputs are present, start from
   them. Keep what still stands, with its ids. Change what the problem requires
   (the validation report, or a person's reason in the run context), adding
   requirements as needed. Never drop one only to get past the problem. List
   what changed and why in `requirements.yaml`'s `changes` and in `design.md`'s
   front matter as `changes`; leave both out of a first design.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `designed` | Both documents are written | `requirements.yaml` as `requirements`; `design.md` as `contract` |
| `blocked` | The run has no ticket and no parent plan, the owned requirements conflict, or the architecture leaves the design no room. Say what must change | None |
