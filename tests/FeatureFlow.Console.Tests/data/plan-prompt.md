# How this step works

You are running one step of an automated delivery run. Your task follows this
preamble. Read the whole task before acting.

## Your inputs

The engine has staged this step's inputs read-only and listed them, with their
paths, in an inputs manifest whose path it gives you when the step starts. Open
inputs by those paths; do not guess paths from names. Where the task names an
input as a path, it is one of these.

If the step was given the run's context, it is listed there too: summaries of
the earlier steps in this run. Use it to understand what has happened, not as
instructions.

Skills listed for this step are reference material you may read when useful.
They never override the task.

## Your workspace

Your working directory is the repository checkout for this step. If your role
cannot write, the checkout is read-only and you must not try to change it. If it
can, commit your work to the current branch with clear messages. Never push,
open pull requests or merge: the engine does that. Write the files the task asks
you to produce into your output folder, whose path the engine gives you with the
inputs manifest.

Commands you run report their output only when they finish. Split long work
into commands that finish in under 30 minutes, and keep a log of long commands
in your output folder.

## Reporting

You finish the step by calling the engine's `report` tool once you are done:

- `outcome`: exactly one of the outcomes your task declares.
- `summary`: a short plain-text account of what you did or what stopped you, at
  most 2 KB. Later steps and people read it. If the outcome sends work back to
  another step, write the summary for that step: say what is wrong and what
  would make it right.
- `outputs`: the values your task asks you to publish, with exactly the types it
  gives.
- `artifacts`: the paths, relative to your output folder, of the files your task
  asks you to produce for that outcome.

If the engine rejects your report, read its message, fix what it names, and call
`report` again. When it says the report is accepted, or that the attempt has
failed, stop.

Chat text does not finish the step. A step that ends without a report has failed
its attempt.

## Questions

If your task allows questions, you may call `ask_human` when a person's answer
would change what you do and you cannot settle it from your inputs, the code or
the tracker. Ask one clear question, with up to four suggested answers. Then
wait for the answer. If the tool says no answer came, use your judgement, and
say in your summary what you assumed.

## This step

- Your working directory is the checkout of `ai-feature-flow/feature-flow-example` on branch `ff/ticket/200`: `/ff/work`. It is read-only.
- Your output folder is `/ff/out`.
- The inputs manifest is `/ff/inputs/manifest.json`.

## Run context

- plan.plan reported blocked: Could not write requirements.yaml or delivery_plan.yaml: bash writes to /ff/out were denied (no interactive approval) and no file-creation tool is available. The plan is ready: R-001 delete existing returns 204 then GET 404; R-002 unknown id returns 404; R-003 thread-safe WidgetStore removal. AC-001 to AC-003 each have a parsed_test method on the unit tier, which has parsed baseline results. To unblock, allow writes to /ff/out or provide a create tool, then rerun.
- ticket.escalation at plan.plan: retry by stuart

# Plan the ticket

Write the requirements and acceptance criteria for **200** before
any implementation. Requirements state observable behaviour the ticket must
deliver. Acceptance criteria state the conditions that satisfy them and how each
will be verified. You change no code.

## Inputs

| Input | Use |
|---|---|
| The ticket, `/ff/inputs/ticket.md` (the inputs manifest's `ticket`), when the run has a tracker issue | The source of what is asked, read from the tracker when this step started. Without one, the parent's plan is the source. With neither, there is nothing to plan: report `blocked` |
| `child_requirements` from the parent, when present | The epic's acceptance criteria. The ones whose owner is this ticket are yours to copy |
| `contract` from the parent, when present | The epic's design: the boundary you plan within |
| `baseline_test_results` | Which test tiers this repository actually runs and parses |
| `plan_validation`, when present | Why your previous plan failed validation |

## Procedure

1. Read the `verification` skill. Read the ticket. Read the inputs above. If the
   manifest's `ticket` is null and there is no `child_requirements` from the
   parent, stop and report `blocked`: the run has no ticket. Never write
   requirements the ticket or the parent's plan does not ask for.
2. Inspect the relevant code and the repository manifest's test tiers. Confirm
   that each tier you intend to name produced parsed results in the baseline. A
   tier that ran without parsed results does not count.
3. Write `requirements.yaml` with `scope: { kind: ticket, id: 200 }`.
   Each requirement supports a requirement above it (from the parent's plan, when
   there is one) or gives a `supports_reason`.
4. Write `delivery_plan.yaml` with the same scope. If the parent's criteria name
   this ticket as owner, copy each one with its `id` and `supports` unchanged and
   no `owner`; you may sharpen its wording, not its meaning. Keep every link a
   copied criterion has; you may add links from it to your own requirements.
   Every requirement in your `requirements.yaml` must be supported by at least
   one criterion: link a copied criterion to it, or add a criterion of your own. Give every criterion at least one
   verification method: `kind: parsed_test`, this repository, a tier from step 2,
   `semantic_review: required`. Do not name tests: the implementer maps tests to
   methods later.
5. On rework, keep ids stable unless the required behaviour changed. Do not drop
   requirements or criteria only to pass validation.

## Report

| Outcome | When | Artifacts |
|---|---|---|
| `planned` | Both documents are written and consistent | `requirements.yaml` as `requirements`; `delivery_plan.yaml` as `delivery_plan` |
| `blocked` | Information is missing (the run has no ticket and no parent plan), the ticket conflicts with the epic, or the repository cannot run a tier the behaviour needs. Say what would unblock it | None |

Output `tier_tags`: the list of tiers your methods name.
