# Review 07: feedback on the configuration schema

Date: 2026-10-26. From: product owner (via a review assistant). To: architecture.
Reviewed: `docs/07-configuration.md` at commit 9c60bd2.

## Summary

Solid. The layers, safety floors, policy model and profiles follow the decisions. Answers to the six open questions are below, followed by four issues found in the schema.

## Answers to section 11

| # | Question | Decision |
|---|---|---|
| 1 | Split organisation and team | No fifth layer. Where teams need different policy, the organisation file may contain team-scoped overrides selected by team (same layer, same file, no extra precedence step) |
| 2 | May a repository tighten a floor | Yes. Any layer may tighten a floor; no layer may relax one. Validation enforces that floors only move in the stricter direction down the layers |
| 3 | Selecting between two CI providers | Generalise: **every provider type may have several named instances**, with one marked default. A repository's `ci.mapping` selects the instance per tier. The same mechanism covers several review providers (for example an AI code review on the PR plus the engine's own reviewer agent) and several agent runtimes (selected by role). Replace `ciSecondary` |
| 4 | Where `defaults.attempts` belongs | Both. The installation provides defaults; the organisation may override them within its ceilings. Defaults are not floors |
| 5 | Budget configuration shape | Deferred with Q16. When added: per run and per step kind at the organisation layer; a run may only lower them; exceeding one always stops at a gate |
| 6 | Per-step dry-run support | Not needed. A dry run executes the same plan against **fake or recording providers**, selected by configuration. This is the same mechanism the workflow testing design will need, so one feature serves both. Remove per-step dry-run declarations |

## Issues

| # | Issue | Change |
|---|---|---|
| a | Merge method and required checks are set both in organisation policy (`policy.merge`) and in `feature-flow.yaml` (`merge:`). Two places for one setting is the prototype's "settings conflicted" failure | The organisation policy states what is **allowed** (methods, whether checks are required). The repository **chooses** within that (its preferred method). A repository choice outside the allowed set is a validation error at run start. Never a silent override |
| b | The Windows tier example has `target: ci` and `os: windows`, and the note says such a run fails at capability negotiation | `os` applies to the `sandbox` execution target only. A Windows tier delegated to CI must work in version 1 (review 03, Q9). Only a Windows **sandbox** requirement fails negotiation |
| c | `temporal.retention: 30d` in the installation schema | Retention is a namespace setting, owned by the platform team on managed Temporal. Treat it as the **expected** retention: check it at startup and warn on mismatch; do not try to apply it |
| d | `workerPlatform.agent.pools[].replicas` and `orchestration.replicas` | Scaling belongs to the deployment (Helm values, a Kubernetes autoscaler, compose). Engine configuration names the task queues and the capabilities each pool serves, not replica counts |
