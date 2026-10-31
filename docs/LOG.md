# Decision log for the product owner

Judgement calls made while writing the design without the product owner
present. Each entry says what was decided, why, and where. Review them; any
you disagree with is changed in the owning document. Entries move to the
decision register (`docs/10`) once reviewed.

Started 2026-10-31, overnight session. Local commits only; nothing pushed.

| # | Document | Decision | Why | Needs a look? |
|---|---|---|---|---|
| 1 | 01 s2.1 | Three step forms: agent step, decision, engine action. `parallel` and fragments are plan constructs, not steps. Starting a child run is an engine action | Matches FF's agent / human / engine split; keeps composition out of the step list | Low |
| 2 | 01 s3.1 | Outcome classes are optional; an outcome without a class gets no default route and must be routed explicitly | Avoids guessing a class from a name | Low |
| 3 | 01 s3.2, s10 | No `replan_requested` outcome and no `resume` directive. Planning is an authority an agent step is given (as in FF); an applied patch names where the run continues. Invalid output or an invalid patch, after correction turns, fails the attempt rather than producing a special outcome | Removes two special cases from the language; one rule for bad agent output | **Yes**: changes how re-planning looks in templates |
| 4 | 01 s3.3 | The engine's only built-in route default: `attempts_exhausted` escalates | FF's `on_exhausted: escalate` default; stopping for a human is always safe | Low |
| 5 | 01 s6 | Ledger split: the engine records findings, matches re-raises by fingerprint and records whether the code changed; whether a re-raise reopens a fixed or declined finding is library policy | Applies RW4 strictly. The standard library keeps the first design's sticky behaviour as its default | Medium |
| 6 | 01 s7 | Six decision kinds (gate, intervention, question, patch approval, action request, breakpoint) and six recovery actions (retry, go to, re-plan, grant attempts, end, continue) | One decision primitive, as RW9 asked; recovery set from FF's interventions plus re-plan and grant | Medium |
| 7 | 01 s1.1 | Run statuses: `running`, `completed`, `failed`, `rejected`, `partial`, `aborted`, `incomplete` | Union of FF's and the first design's; `aborted` from FF | Low |
| 8 | 03 s2.1 | A step's form is set by which key it has (`agent`, `decision`, `action`), instead of a `kind` field. One `with` key holds inputs for every form | Shorter, and the form-specific settings sit under the form's key | Low |
| 9 | 03 s4.3 | Route resolution order: step route, plan default by outcome name, plan default by class, next in list for `success`, engine default. A `success` outcome on the last step defaults to `{ end: completed }` | The last-step default keeps short templates short; it is safe because the outcome is class `success` | **Yes**: the first design required an explicit `end` everywhere (DR6). The `incomplete` status still catches dead ends |
| 10 | 03 s4.1 | `clean_up` defaults to `false`. FF derived it from step positions | FF's derivation would discard a fix when a fix loop routes back to an earlier test step | Low |
| 11 | 03 s5.4 | Repository facts reach routing through a `repo.inspect` action that publishes outputs | Keeps "expressions never read artifacts" and "manifest read per step" | Low |
| 12 | 03 s8.2 | `for_each` over a list known at binding, expanded into `parallel` or a chain. Ordered delivery is a chain | Fixes the one-repository-per-ticket gap with one construct; the first design rejected a sequential mode on `parallel`, and a chain keeps order as routes | Medium |
| 13 | 03 s10.1 | `resume_at` is required on every patch, except one sent to a parent | Removes the guessed default the first design had | Low |
| 14 | 03 s10.2 | Six patch operations: `insert` (before/after), `replace`, `remove`, `set` (one field), `add_branch`/`remove_branch`, `chain`. No role change by patch | Down from eleven; `set` covers the separate replace-inputs, replace-limits and change-route operations | Medium |
| 15 | 03 s11 | Fragments: expanded at load, ids prefixed `<id>.`, named exits routed like outcomes, nesting up to 3 | Needed for RW5's "standard review"; expansion at load keeps validation simple | Medium |
