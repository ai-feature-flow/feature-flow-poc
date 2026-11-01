# Plan the epic's tickets

Divide the epic into tickets and write the acceptance criteria each ticket owns.
You change no code. The work list you write becomes the ticket runs, with no
agent in between.

## Inputs

| Input | Use |
|---|---|
| `requirements` | The epic's requirements, from its design step |
| `contract` | The epic's design: the tickets deliver it |
| Earlier work list and validation reports, when present | Why a previous plan was sent back |

## Procedure

1. Read the `solution_design` and `verification` skills, then the inputs.
2. Derive cohesive tickets from the design, each reviewable on its own. Give each
   a short stable `name` (snake_case), its repositories, a concrete `boundary`,
   and its dependencies on other tickets. Where the epic needs integration
   capability (an integration environment or an integration tier), plan it as
   tickets ordered before the work that depends on it.
3. Write `child_requirements.yaml` (a delivery plan) with
   `scope: { kind: epic, id: <the epic's scope id> }`, exactly the scope of the
   epic's requirements. For every epic requirement write one or more acceptance
   criteria supporting it. Give each criterion exactly one `owner`: the ticket
   whose boundary delivers it, or the epic itself only when the epic's own
   integration run can prove it. Owned criteria carry no verification methods:
   the owning ticket adds them.
4. **Every ticket must own at least one criterion**, or it cannot run. For work
   with no natural criterion (configuration, tooling), write one a unit or
   component test can show, or fold the work into a ticket that owns one.
5. Write `work_list.yaml` with `level: ticket` and one item per ticket. In each
   item's `owns`, list the criteria it owns. Put the tracker key in
   `tracker_issue` where you created or found one.
6. On rework after a ticket failed, change only that ticket and what depends on
   it. Keep criterion ids stable and completed tickets' boundaries unchanged.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `planned` | Both documents are written and every ticket owns a criterion | `work_list.yaml` as `work_list`; `child_requirements.yaml` as `child_requirements` |
| `blocked` | The design cannot be divided as it stands. Say what must change | None |
