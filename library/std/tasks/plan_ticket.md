# Plan the ticket

Write the requirements and acceptance criteria for **{{ subject_id }}** before
any implementation. Requirements state observable behaviour the ticket must
deliver. Acceptance criteria state the conditions that satisfy them and how each
will be verified. You change no code.

## Inputs

| Input | Use |
|---|---|
| The ticket, `/ff/inputs/ticket.md` (the inputs manifest's `ticket`), when the run has a tracker issue | The source of what is asked, read from the tracker when this step started. Without one, the parent's plan is the source |
| `child_requirements` from the parent, when present | The epic's acceptance criteria. The ones whose owner is this ticket are yours to copy |
| `contract` from the parent, when present | The epic's design: the boundary you plan within |
| `baseline_test_results` | Which test tiers this repository actually runs and parses |
| `plan_validation`, when present | Why your previous plan failed validation |

## Procedure

1. Read the `verification` skill. Read the ticket. Read the inputs above.
2. Inspect the relevant code and the repository manifest's test tiers. Confirm
   that each tier you intend to name produced parsed results in the baseline. A
   tier that ran without parsed results does not count.
3. Write `requirements.yaml` with `scope: { kind: ticket, id: {{ subject_id }} }`.
   Each requirement supports a requirement above it (from the parent's plan, when
   there is one) or gives a `supports_reason`.
4. Write `delivery_plan.yaml` with the same scope. If the parent's criteria name
   this ticket as owner, copy each one with its `id` and `supports` unchanged and
   no `owner`; you may sharpen its wording, not its meaning. Add criteria of your
   own where the ticket needs them. Give every criterion at least one
   verification method: `kind: parsed_test`, this repository, a tier from step 2,
   `semantic_review: required`. Do not name tests: the implementer maps tests to
   methods later.
5. On rework, keep ids stable unless the required behaviour changed. Do not drop
   requirements or criteria only to pass validation.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `planned` | Both documents are written and consistent | `requirements.yaml` as `requirements`; `delivery_plan.yaml` as `delivery_plan` |
| `blocked` | Information is missing, the ticket conflicts with the epic, or the repository cannot run a tier the behaviour needs. Say what would unblock it | None |

Output `tier_tags`: the list of tiers your methods name.
