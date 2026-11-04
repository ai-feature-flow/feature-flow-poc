# M7 results: people, policy and steering

## Shared interfaces

What M7's five builders share, built first on `m7-interfaces` (W0), with fakes, contract suites and file splits, and no
feature implementations. The builders: W1 steering core, W2 policy and identity, W3 surfaces, W4 interactive sessions,
W5 reports and triage.

**What was built**

| Area | Types | Used by |
|---|---|---|
| Notifications (docs/06 section 3.10) | `INotificationsProvider` (send, post a decision; neither waits), `MessageRef`, `Notification`, `DecisionRequest`, `NotificationEvents` (the six event names and the de-duplication key: run, step, attempt, event). Fake `InMemoryNotifications`, kind `memory` | W3 |
| Session surface (docs/06 section 3.11) | `ISessionSurfaceProvider.PrepareAsync`, idempotent by session id; `SessionWorkspace`, `SessionFile`, `SessionHandle`. Fake `InMemorySessionSurface`, kind `memory` | W4 |
| Authentication | `IAuthenticator` (the first to accept a request decides its caller); `LocalTokenAuthenticator` (today's behaviour); `Caller` gains `Groups` and `Channel` (`api` or `chat`: the chat gateway acts as the mapped person, never itself); `IPeopleRoles.RolesFor(caller, RoleScope)` with `CredentialRoles` as the default; `ApiServices.Authenticators` and `PeopleRoles` | W2 (OIDC, roles), W3 (chat gateway) |
| Organisation configuration (docs/07 section 3) | `OrganisationConfig` gains roles, role mapping, notifications, timeouts, teams, library overrides and inputs; `PolicyConfig` gains `Overrides`, `RequireReview`, `AllowedToolsets`; `TeamConfig` (with `repositories`, LOG 204); `DecisionTypeItem.DefaultApproval`. The reader still refuses these keys | W2 fills; W1, W3 read |
| Override catalogue and wire | `Model.Overrides`: every key of docs/05 section 5, default roles, variants (`*_protected`, `grant_attempts_beyond`), `RolesFor` with `policy.overrides`. Update `override` with `OverrideArgs`; `AnswerArgs` gains `Artifacts` and `Revision`; `AbortArgs` gains `ClosePullRequests` | W1, W2, W4, W5 |
| Record store | `OverrideRecord` (appended), `NotificationRecord` (once per key, first write wins), `SessionRecord` and `SessionCheckRecord` (last write wins); queries `overrides`, `notification`, `notifications`, `session`, `sessions`, `session_checks`; SQLite and in-memory | W1, W2, W3, W4 |
| Wake signal | Signal `wake` with `WakeSignal(source, event, step?)`: a hint; the wait reads again | W3 |
| File splits | `ApiApp` split into runs, decisions, overrides, sessions, inbound, console and authentication files, each mapping its own routes; `RunWorkflow` partial with empty overrides, sessions and inbound files. Pure moves | All |

**Checks**

- Contract suites: `NotificationsContract` (4 tests), `SessionSurfaceContract` (2), `SteeringRecordsContract` (4), each
  against the fakes; the records suite also against SQLite.
- `OverridesTests` (11), the API's authenticator order and refusal (1), the fakes composing by world (1).

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| Fake notifications: a reply posted at the top level; an unknown channel accepted; a decision post without its wait id | `NotificationsContract` |
| Fake session surface: a new handle on every prepare; instructions not naming the session | `SessionSurfaceContract` |
| In-memory store: a notification's last write wins; an override replaced by id; sessions of any run | `SteeringRecordsContract` |
| SQLite store: notifications replaced; sessions first-write-wins; checks of any session; overrides of any run | `SteeringRecordsContract` (SQLite) |
| The de-duplication key without the attempt | `SteeringRecordsContract` |
| The catalogue without admin implied, ignoring the organisation, missing a variant | `OverridesTests` |
| The API's last authenticator winning | `ApiTests` |

**Review fixes (R2, changes-required)**

| Finding | Fix | Control (mutation that failed the test) |
|---|---|---|
| F1 a team rule could relax by its roles or a broader pattern | A team rule sets only `min_count` and only raises it | The team rule governing whole; a team rule allowed `roles` (`OrganisationTests`) |
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children with its own message, re-check N3) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`); run start skipping it (`OneTeamRunStartTests`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | Re-check N1: one `RequestRefresh` on an unknown key, no immediate retry; the next requests see the rotated key | No refresh (`OidcTests.A_token_signed_by_a_rotated_key_is_accepted_once_the_keys_are_read_again`) |
| F7 min_count race | Follow-up, LOG 221 | — |
| F8 / C7 a grant at an intervention needed only `grant_attempts` | Needs `recover` and `grant_attempts` | Either check off (`Granting_attempts_at_an_intervention_needs_recover_and_grant_attempts`) |
| M10, M11 passed by construction | Tests: a viewer may not start a run; the token's issuer is the configured one even when the metadata names another (the equality check is gone, `ValidIssuer` is the configured issuer) | Start needing only viewer; the issuer taken from the metadata |

The round-1 mutation script restored files with an older timestamp, so a later run could keep an earlier mutation's build.
After the fix, the round-1 controls were run again; each still failed its own test.

**Deviations and judgement calls:** LOG 201 (de-duplication in the engine), 202 (sessions; one answer payload for what a
person produced, triage included), 203 (one catalogue, one override update), 204 (authenticators; scoped roles; teams
gain repositories), 205 (wake signal; file splits).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | The override update, wake signal and their handlers have no implementation yet: W1 (`override`), W3 (`wake`). Until then Temporal rejects an `override` update and holds a `wake` signal unhandled |
| 2 | `docs/07` should list `teams.<name>.repositories` once LOG 204 is approved |

## Steering core

What W1 built on `m7-steering`: a person's run-time overrides in the interpreter and the run workflow (docs/05 section 5),
breakpoints, mid-run library overrides, the gaps in agent proposals (F3, F10), and the two recoveries M6's live runs
needed. Everything that old histories could reach is behind the patch marker `m7-steering`.

**What was built**

| Area | What |
|---|---|
| The `override` update | `RunWorkflow.Overrides.cs` carries out `breakpoint`, `skip`, `amend`, `change_role`, `grant_attempts`, `override_library` and `abort` through the core, with the actor the API authenticated (F8). It refuses protected content or a grant beyond the hard ceiling unless the caller holds the variant (`skip_protected`, `amend_protected`, `override_library_protected`, `grant_attempts_beyond`), names the protected steps in its reply, is idempotent by override id, and records each on the timeline. Answers and recoveries stay on the `answer` update (LOG 203) |
| Breakpoints | The run pauses before the step and opens a `<level>.breakpoint` wait, answered with `continue` or any recovery; it holds until cleared (LOG 206) |
| Skips, grants, role changes, amends | A skip ends the step with a chosen outcome, using no attempt (LOG 207). One grant mechanism for the override and the recovery (LOG 208). `change_role` is a person's patch; `amend` may omit `resume_at` and never changes a role (LOG 209) |
| Mid-run library override | Content stored by hash, the run's lock rewritten (its last lock record is the run's lock), future steps resolve the new content while steps in flight keep theirs, running children are told by signal, later children inherit the lock; a lock entry with actor, reason, old and new hash, steps in flight and protected steps affected (LOG 212) |
| Agent proposals | An agent patch that routes around a protected step, or falls under the organisation's `planning.require_review`, awaits a person (LOG 210, 211). An agent's library override is always a patch approval, applied as the approver's (LOG 213) |
| Recoveries | Going to a failed branch of a decided `parallel` or chain reopens its join, so the run can complete (LOG 214). An abort may close the run tree's open pull requests (LOG 215) |
| Re-drive tests | The workflow test format gains `overrides`, `answers` and `expect.skipped` (docs/09 section 4.3); std gains `ticket_breakpoint_before_merge`, `ticket_skip_protected_analysis` and `epic_child_rerun` |

**Checks**

- `SteeringTests` (21, interpreter core), `RequireReviewTests` (1), the three std re-drive tests (`ff test library/std/tests`, 25).
- `SteeringRunTests` (7, Temporal): a breakpoint and a protected skip; amend, role change and grant within their variants; a
  library override reaching a later step and a running child while a step in flight keeps its task; an agent's library
  override approved by a person; re-running a failed child of a join; an abort closing the run tree's pull requests (and
  one leaving them); an override sent while the run drains for a hand-over, carried into the next run.
- Two recorded histories, `steering_line` and `steering_rerun_branch`, replay; every older history still replays.

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| No pause at a breakpoint; a skip never applied; a decided join not reopened | `SteeringTests` and the matching std re-drive test |
| A protected skip, or a person's protected amend, without the variant; an amend changing a role; a grant above the hard ceiling | `SteeringTests` |
| Routing around a protected step allowed; the organisation's `require_review` ignored; breakpoints not carried in a snapshot | `SteeringTests` |
| Reopening a join with the marker's path off | `Replay.Tests` (`steering_rerun_branch` does not replay) |
| Every variant treated as held | `SteeringRunTests`: the protected skip is not refused |
| The core not resolving from the new libraries | `SteeringRunTests`: the review still gets `REVIEW-V1` |
| Pull requests never closed; children not told to close theirs | `SteeringRunTests`: the abort test |
| Breakpoints not carried across a hand-over | `SteeringRunTests`: the hand-over test's run never pauses |

**Deviations and judgement calls:** LOG 206 to 215. Additive changes outside the per-area files: `RunPolicy.RequireReview`
(`Wire.cs`, not in the frozen list), `StepReport.LibraryOverride` and the scripted runtime's `library_override`.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | W2's API: pass the caller's variant keys in `OverrideArgs.Variants`, and `close_pull_requests` from the abort request into `AbortArgs.ClosePullRequests` (today's endpoint does not) |
| 2 | An MCP tool for agents to propose a library override (and a patch), so real runtimes can use LOG 213's path |
| 3 | `run.child`'s `run_gate: manual` (a breakpoint before a child's first step) is not built |
| 4 | F1 at patch time: a patch adding an engine merge into a default branch is caught only at merge time (`scm.merge` re-checks), not when proposed |
| 5 | An abort's hold on an action that sends no heartbeat waits for the action to end before the run closes its pull requests (M3 follow-up 9) |
| 6 | From R1, low: a replaced or inserted container whose inner agent steps change role is not caught by the role checks (top-level `agent.role` only, pre-existing); branches of a `protected: true` parallel are not themselves protected (std protects fragments, which propagate the flag); a protected step inside one branch of a parallel is checked through its parallel, not path by path (LOG 210); an `override_library` sent twice with one id while the first awaits its activity records two entries (harmless; W2 can use the override id as the update id) |

**Review fixes (R1, changes-required)**

| Finding | Fix | Control (mutation that made the check fail) |
|---|---|---|
| F1: an agent patch could leave a protected step unrun by a never-true guard, a bypass one step earlier, `resume_at`, or a fake guard | One reachability check replaces route-edge matching (LOG 210); the four probes are tests | No protected step tracked; guarded routes followed by their first entry only |
| F2: a breakpoint plus go-to left a protected step unrun without `skip_protected` | A go-to or re-plan that leaves one unrun is refused; skip it first (LOG 206) | The refusal off |
| F3: an amend's `resume_at` forked a running run | Refused at the override update (LOG 209) | The refusal off |
| F4: a library override reached children's protected steps | Needs `override_library_protected` in a run that starts children (LOG 212) | Children not counted |
| F5: a steerer approving an agent's proposal reached what only an administrator may change | std's `*.patch_approval` defaults to `admin` (LOG 213) | std back to `steerer` |
| F6, F7: ended children's pull requests left open; any `pull_request` output counted | Children answer a `pull_requests` query; only `scm.pull_request` and `artifact.publish` count (LOG 215) | Children not read; the action filter off |
| F8: a skip on a pre-M7 run was accepted, then ignored | Refused (LOG 207) | The refusal off |
| Untested: the lock across a hand-over; a carried `PastBreakpoint` entry and breakpoint `clean_up`; a skip during a patch approval; a reopened branch's status | Tests added | Each removed in turn |

## Policy and identity

W2, branch `m7-policy`: the organisation file in full, people roles, approval rules, authorisation of every run-time
override, OIDC, and the effective policy view.

**What was built**

| Area | What | Where |
|---|---|---|
| Organisation file (docs/07 sections 3 and 7) | Every key read into the frozen model, unknown keys refused, caps as a value or `{ value, locked }`. People roles are the four of docs/05 section 3.3. A team's approval rule sets only `min_count`, and only raises it. Startup checks across the two layers name the layer, key and reason (LOG 223) | `Model/Configuration.cs`, `Hosting/StartupChecks.cs` |
| People roles | From identity-provider groups; in a team's scope its groups add holders. `admin` > `steerer` > `viewer`, `approver` apart. A run spanning two teams is refused at start (LOG 216, 217) | `Model/Approval.cs` (`PeopleRoles`), `Api/ApiAuthentication.cs` (`OrganisationRoles`) |
| Approval | One resolver for the API, run start and the view: the organisation's rule, the library's `default_approval`, the engine's; a team only raises `min_count`. `min_count` counts distinct people answering the same way, each recorded; `from_chat` against the caller's channel; no caller may act as `policy` (LOG 221, 222) | `Model.Approval`, `ApiApp.Decisions.cs` (`SubmitAnswerAsync`) |
| Override authorisation | `POST /runs/{id}/overrides` for the keys the `override` update carries; default roles, `policy.overrides`, and the variants the caller holds passed to the run. A recovery at an intervention is the `recover` override (LOG 218, 225). Reading needs `viewer`, starting a run `steerer` (LOG 219) | `ApiApp.Overrides.cs`, `ApiApp.Authorisation.cs`, `ApiApp.Runs.cs` |
| OIDC | Discovery, keys, issuer, audience, expiry and signature; actor and groups from configured claims; beside the local token (LOG 220) | `Api/ApiAuthentication.cs`, `api.auth` in the installation |
| Effective policy view | Per merge step (who merges, checks) and decision step (who answers, auto): request, cap, result, deciding setting. In `ff show --organisation <file> [--repo <r>]` and in the run start record as `policy` timeline entries, patch `m7-effective-policy` (LOG 224) | `Validation/EffectivePolicy.cs`, `Engine/Activities.cs` |
| In-run values | A run's auto-resolution rules follow the same order, fixed at start; check 22 uses the governing rule and warns where an unlocked rule makes `auto` have no effect (LOG 222) | `Engine/EngineServices.cs`, `Validation/RunStartChecks.cs` |
| CLI | `ff run override <run> <key> --reason <text> [--step <id>] [--change name=value]...`; `ff run abort --close-pull-requests` | `Cli/RunCommands.cs` |

**Checks**

- Done-when 1, the API side: `OverrideAuthorisationTests` gives every key of docs/05 section 5 a role that may and one
  that may not, the four variants (a steerer holds none, an administrator each), `policy.overrides` changing a key and a
  variant, and roles scoped by the run's team. A fake Temporal client keeps the delivered arguments, so "may" is shown by
  the update the run receives.
- Verified-by: a body asserting `mallory` is recorded and delivered as the authenticated caller, for answers, recoveries,
  aborts and every override (`OverrideAuthorisationTests`; through a real run in `PolicyRunTests`, Temporal :8333).
- `ApprovalTests` (min_count, chat, rule precedence, team rule, `policy`), `OidcTests` (in-process issuer: valid, expired,
  wrong audience, wrong issuer, foreign key, unsigned, no subject, `policy`, local token beside), `OrganisationTests`,
  `StartupCheckTests`, `EffectivePolicyTests`, `RunPolicyTests`, `ShowPolicyTests`, `PolicyRunTests` (the run start record;
  a team's rule deciding auto-resolution in a real run; the CLI's override and abort).
- Default suite 1,077 passed (replay 19); integration 81 passed (Temporal :8333).

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| A team rule allowed to relax `min_count`; team roles added to, not replacing, the organisation's; `admin` implying `approver`; organisation auto rules before the team's; an unknown `policy.overrides` key accepted | `OrganisationTests` |
| A `role_mapping` runtime unchecked at startup | `StartupCheckTests` |
| The actor taken from the body: answers (in both places it is set), overrides, aborts | `OverrideAuthorisationTests`, `OidcTests` |
| No role check; every variant passed; recovery by approval rule instead of `recover` | `OverrideAuthorisationTests` (19, 4 and 1 tests) |
| `min_count` counting a person twice, or an answer given another way; `from_chat` unchecked; `policy` allowed; roles not scoped by team | `ApprovalTests`, `OverrideAuthorisationTests` |
| OIDC lifetime, audience or issuer unchecked; an invalid token accepted; groups ignored | `OidcTests` |
| Run auto rules without library defaults or the team; check 22 silent on an unlocked refusal; the view resolving auto whatever the rule or ignoring `engine_may_merge`; `ff show` ignoring the team | `RunPolicyTests`, `RunStartCheckTests`, `EffectivePolicyTests`, `ShowPolicyTests` |
| The view not written to the run start record; in-run auto ignoring the team; the CLI dropping the step, splitting a change at its last `=`, never asking to close pull requests | `PolicyRunTests` |

**Review fixes (R2, changes-required)**

| Finding | Fix | Control (mutation that failed the test) |
|---|---|---|
| F1 a team rule could relax by its roles or a broader pattern | A team rule sets only `min_count` and only raises it | The team rule governing whole; a team rule allowed `roles` (`OrganisationTests`) |
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children with its own message, re-check N3) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`); run start skipping it (`OneTeamRunStartTests`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | Re-check N1: one `RequestRefresh` on an unknown key, no immediate retry; the next requests see the rotated key | No refresh (`OidcTests.A_token_signed_by_a_rotated_key_is_accepted_once_the_keys_are_read_again`) |
| F7 min_count race | Follow-up, LOG 221 | — |
| F8 / C7 a grant at an intervention needed only `grant_attempts` | Needs `recover` and `grant_attempts` | Either check off (`Granting_attempts_at_an_intervention_needs_recover_and_grant_attempts`) |
| M10, M11 passed by construction | Tests: a viewer may not start a run; the token's issuer is the configured one even when the metadata names another (the equality check is gone, `ValidIssuer` is the configured issuer) | Start needing only viewer; the issuer taken from the metadata |

The round-1 mutation script restored files with an older timestamp, so a later run could keep an earlier mutation's build.
After the fix, the round-1 controls were run again; each still failed its own test.

**Deviations and judgement calls:** LOG 216 (role nesting, approver apart), 217 (team scope and overlays), 218 (an
intervention is answered as `recover`), 219 (viewer to read, steerer to start), 220 (OIDC), 221 (min_count recording),
222 (library default in the run's auto rules; unlocked warns; `policy` reserved), 223 (file read in full; keys read but not
yet enforced), 224 (effective policy view), 225 (override wire and CLI).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | W1: the run refuses protected content or a beyond-ceiling grant without the variant in `OverrideArgs.Variants`; until its handler lands, Temporal rejects the `override` update (the API records the refusal). Auto-resolution's timeline entry gives the outcome but not the actor `policy` (docs/04 section 4.2) |
| 2 | W3, W4, W5: answers from the chat gateway, a session's submission and a person's triage should go through `SubmitAnswerAsync`, so approval, `from_chat` and `min_count` apply; the gateway's `Caller` needs the person's groups (or roles) for team scoping |
| 3 | Read but not enforced on this branch (LOG 223): `role_mapping`, `timeouts`, `library_overrides`, `library_inputs`, `toolsets.allowed`; check at integration which of the others W1 and W3 consumed |
| 4 | `merge.require_checks` locked on refuses only a literal `require_checks: false` at run start; an expression giving false at merge time is not checked against the cap (`scm.merge` does not read it). Older than M7 |
| 5 | Startup does not yet resolve the API's `token_ref` or other secret references (docs/07 section 7); a missing one shows as every local-token request refused |
| 6 | min_count: two answers at the same moment can leave the count short until another answer; re-read after recording (review R2 F7) |
