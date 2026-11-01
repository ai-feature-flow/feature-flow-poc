# Approve the plan for {{ params.ticket_id }}

The planner has written the requirements and acceptance criteria for
**{{ params.ticket_id }}**, and the plan passed its validators. Approving
ratifies these exact revisions: implementation starts against them, and they can
change only through a new approval.

Check:

- Each requirement states observable behaviour the ticket needs, and nothing it
  does not.
- Each acceptance criterion supports a requirement and is testable.
- Each verification method names a test tier that the baseline run shows the
  repository can run and parse.
- Where the ticket belongs to an epic, the criteria it copied from the epic keep
  their meaning.

| Answer | Effect |
|---|---|
| `approved` | Implementation starts |
| `rejected` | The plan goes back to the planner, with your reason, and the work branch is reset to where planning started |
