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
