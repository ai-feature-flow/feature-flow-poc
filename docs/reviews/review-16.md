# Review 16: Spike 2 re-run with a real model

Date: 2026-10-30. From: product owner (via a review assistant). To: architecture.
Reviewed: branch `ff/epic/spike-2` at f264e22, and the fixture repository's pull requests.

## Summary

**Spike 2 has answered its question: yes, the design is feasible against real providers.** A model authored the change (Copilot CLI on `z-ai/glm-5.3-flash` through OpenRouter, `WidgetStore.IsEmpty()` and its regression test), the plumbing around it is real, and a PR has been merged through the engine (PR #3). The two halves were shown in different runs; that is enough for feasibility. **Close Spike 2.**

## Review depth from here

Decided by the product owner, 2026-10-30. Spikes prove that a design concept is feasible before the design is formalised and the application is built. Reviews of spikes therefore ask two questions only:

1. Does the evidence support the feasibility claim? (A check that passes by construction does not.)
2. Did the spike teach something that changes the design?

Production concerns found along the way are **recorded as design requirements**, not fixed in spike code. Keep spike code as cheap as it can be while the evidence stays honest.

## Design requirements recorded from Spike 2

Add these to the design (`docs/01` and `docs/08`), not to the spike:

| # | Requirement |
|---|---|
| R1 | **Model credentials never enter the agent's environment.** Spike 2 passed the OpenRouter key as an environment variable with all tools allowed. Version 1 uses a proxy that adds the key, or per-run keys with a limit and expiry |
| R2 | **The F1 floor is enforced by the engine at merge time**, reading the base and default branch from the code host, and backed by code host protection on the default branch. Done in the spike; keep it in the design |
| R3 | **Code host protection is configured by an identity the engine does not hold** (the parked review 15 A2). Record as a requirement; no spike action |
| R4 | **Review verdicts are unstable with a real model** (1, 1, 1, 2 findings on unchanged input; a fixed finding re-raised). This is the prototype lesson, now measured. Add to the risk register; the findings ledger's handling of re-raised findings is a Spike 3 or design question |

## Decisions

| Item | Decision |
|---|---|
| Claude Code | **Deferred by the product owner, 2026-10-29:** no Anthropic credential on the machine, so Copilot CLI is used. The runtime stays pluggable. Not a spike gap |
| OpenRouter key | A dedicated Feature Flow key exists. Its limit is managed by the product owner |

## Housekeeping

- This work is on a branch of the design repository, `ff/epic/spike-2`, not `main`. Merge it into `main` (after review 15 and this review) and delete the branch. Keep design-repository work on `main`.
- B4 from review 14 (the `docs/15` 6.3 slot correction) and the K4 decline-path re-run: do them only if Spike 3 needs them.

## Next

Propose the Spike 3 plan (`docs/08` 2.3, concurrent tickets with the seeded cross-ticket defect) in light of what Spikes 1 and 2 changed, before building it.
