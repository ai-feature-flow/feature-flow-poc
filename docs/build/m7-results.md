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
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | The dead refresh removed; LOG 220 corrected; follow-up | — |
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
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | The dead refresh removed; LOG 220 corrected; follow-up | — |
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
| 6 | OIDC: a rotated key is accepted only after the metadata cache refreshes (review R2 F6) |
| 7 | min_count: two answers at the same moment can leave the count short until another answer; re-read after recording (review R2 F7) |
| 8 | The run-start refusal of a child spanning two teams has no test of its own (the API's refusal and `Approval.MoreThanOneTeam` are tested) |
