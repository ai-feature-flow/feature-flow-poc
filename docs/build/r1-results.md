# R1 results: extension points

R1, "Extension points" (`docs/12-build-plan-phase-2.md` section 4), built 2026-11-24 in three increments by seven
builders, with an independent review of each and a fix pass after each review. An organisation can now add a provider
without forking the engine, building against the engine's packages; it can change one std task or role in every std
template without copying them; record kinds are declared once; capabilities are checked on the instance a step
actually uses; the classes of finding that need a person are workflow policy; the test doubles are out of the
production assemblies; and tracker statuses are no longer std's names inside the providers. Judgement calls are LOG 548
to 570, unreviewed; **LOG 563 and 570 need the product owner's look**: an installation with a tracker and no `status_map` must
add one (section 5). No replay history changed.

## 1. What was built

| Item | Built | LOG |
|---|---|---|
| F17, record kinds declared once | `RecordKinds` declares each of the 20 `RecordBatch` lists once (table, key, columns, write rule, change effect, run id) and `RecordQueries` every named query but the three store-native ones (`open_waits`, `changes`, `latest_change`). SQLite and Postgres each have one writer and one query builder reading the declarations and keep only their dialect; `ChangeKinds.Of` is computed from them. Tables and columns are unchanged: no migration, and a SQLite file written before R1 reads identically. Store code fell from 927 to 582 lines. The in-memory record store is deleted; tests use SQLite on a temporary file. Every text order is ordinal on both stores | 548 to 551 |
| F16, capabilities | The capability records hold only the flags the engine reads: `agent_runtime.hooks` and `agent_runtime.mcp` (every agent step needs them) and `code_host.pull_request` and `code_host.check_status` (named by action descriptors). The other 33 flags, the non-boolean fields and `BranchProtection.RequiresMergeQueue` are removed. Check 21 asks each need of the instance that serves it: an agent step's runtime needs of the instance its role maps to, naming the `role_mapping` entry used | 552, 553 |
| F13, person classes as policy | `findings.triage` takes `person_decides` (`escalated`, `declined_blocking`, `declined_demonstrated`; unset means all three), read by `complete: true`; both inputs are literal, refused at load otherwise. `declined_blocking` counts blocking declines only, and `declined_demonstrated` is a new output. One reader and one rule (`FindingClasses.PersonDecides`, `FindingsLedger`) serve the engine and the API. std names all three classes and adds the new output to its guard, so std routes as before | 554 to 556 |
| L4, test doubles | The `memory`, `fake`, `scripted` and `recording` providers move to `FeatureFlow.Providers.Testing`, kinds and settings unchanged. Each of their factories is `DevelopmentOnly`, refused by composition unless `development: true`, replacing the configuration reader's list of kind names (which had missed `recording`). No `src/` project references the doubles; the engine's hosts keep them for development installations such as the k3d smoke installation | 557, 558 |
| F6, providers without a fork | `FeatureFlow.Providers.Standard` holds `StandardProviders.Factories`, every standard provider's factories in the hosts' order; the three hosts compose it with `TestingProviders.Factories`. Every library under `src/` is packable (27 packages, version 0.1.0); the hosts, `ff` and `ff-sandbox` are not. `samples/distribution` is an organisation's host (Acme) with a validator provider of its own, built against the packages only. Nothing is published to a feed | 566 to 568 |
| F15, std as fragment wrappers | A template may give one document-level `use` of a fragment in place of `steps`; the fragment's steps join the template unprefixed, so every std step id is unchanged. std's three templates are wrappers of `ticket_flow`, `epic_flow` and `project_flow`, whose roles, tasks and child templates are reference parameters with std's defaults, passed through the inner fragments. A wrapper gives the parameters its fragment reads as `params.<name>` as its own, with their defaults; binding one in `with` is refused | 559 to 562, 569 |
| L8, tracker status identity | The GitHub Issues and Jira trackers lose their built-in maps: a status the `status_map` does not name is the tracker status of the same name. A tracker whose `status_map` names none of the statuses a run's plan moves issues to is refused at run start, naming the instance and `status_map`. Every shipped configuration with a tracker states today's map; hosts warn at start-up for a tracker with no `status_map` | 563 to 565, 570 |
| Also | Two std workflow tests: a declined blocking finding, and a demonstrated non-blocking one, go to a person (`ticket_declined_blocking_to_person`, `ticket_declined_demonstrated_to_person`); the second guards a route no test covered | None |

## 2. Checks

| Done when (`docs/12` R1) | Test |
|---|---|
| The sample host adds a provider of its own and runs a workflow test with it, built against the published packages only | `DistributionTests.The_sample_host_runs_its_workflow_test_built_from_the_packages_only` (integration): packs the solution to a temporary folder, copies `samples/distribution` outside the repository, restores with its own package cache (engine packages from the folder only), runs `Acme.Host.Tests`, and asserts every `FeatureFlow.*` in the sample's assets file is a package |
| Each store passes the record contract suite from the declarations | `RecordKindContract` (a case per declared kind's write rule and per query form; a fact fails a kind or query with no case; every named query is declared or store-native), run as `SqliteRecordKindContract` and `PostgresRecordKindContract`; the five earlier record contracts unchanged on both |
| An organisation library that wraps one std task changes it in every std template without copying them, shown by a load test | `WrapperTemplateTests.A_library_that_wraps_one_std_task_changes_it_in_every_std_template` (fixture `acme_wraps_review`); the 41 earlier std workflow tests and the golden readable forms unchanged |
| A step whose role maps to an instance lacking a required capability is refused at run start although the type's default instance has it | `RoleInstanceCapabilityTests.A_step_whose_role_maps_to_an_instance_lacking_a_capability_is_refused_at_run_start`, and its converse |
| Others | `A_person_triage_must_decide_exactly_the_classes_its_step_names`; `Every_development_only_kind_is_refused_outside_development`; `No_src_project_references_the_test_doubles`; `StandardProviders` lists every provider package; `An_unmapped_status_maps_to_itself` per tracker; the run-start `status_map` refusal |

## 3. Controls

Each named test was seen to fail with the code it guards broken, then pass restored: by the builders and fix passes
(about 30 checks, in their reports), by the reviewers (probe tests, two of which found the medium findings below), and
by the sub-orchestrator on the merged branch (seven): the capability check given the default instance again (both
role-instance tests fail); activity's write rule changed (the SQLite contract fails); the triage rule ignoring its
classes (the done-when theory fails); composition's development-only check off (22 cases fail); `epic_flow` not
passing `reviewer_task` on (the wrapper load test fails); the `status_map` refusal off (Validation and Engine tests
fail); the sample's validator always reporting (`Acme.Host.Tests` fails inside `DistributionTests`, so the packed
sample's test really runs).

## 4. Verification

| Suite | Result at the head |
|---|---|
| Default (`Category!=Integration&Category!=Live&Category!=Cluster`) | 2,254 passed, 0 failed, 26 projects |
| Integration category (local Temporal dev server) | 262 passed, 0 failed: Integration.Tests 138, Postgres 92, Docker 17, S3 12, Api 2, Hosting 1 (`DistributionTests`) |
| Cluster category (own k3d cluster with the smoke installation, deleted after) | 24 passed, 0 failed: Kubernetes 17, Integration.Tests 7 |
| Replay | 47 passed; no history re-recorded |

On earlier heads under heavy local load, a few console browser tests and `StuckRunTests` timed out once each; every
one passed when run alone, and the final head ran clean.

## 5. Upgrading an installation (L8)

An installation with a tracker and no `status_map` relied on a built-in map, which R1 removes. **Add the map before
deploying R1**: a new std run is refused at run start until it is added, and a run already in flight is not checked
again, so its later transitions would go to statuses named as the library names them (on GitHub, `done` would add a
label `done` and leave the issue open). Hosts log a warning at start-up for each tracker instance with no `status_map`.

GitHub Issues:

```yaml
providers:
  tracker:
    instances:
      github_issues:   # the instance's existing settings, plus:
        status_map: { in_progress: "status: in progress", code_review: "status: code review", done: closed }
```

Jira:

```yaml
providers:
  tracker:
    instances:
      jira:   # the instance's existing settings, plus:
        status_map: { in_progress: "In Progress", code_review: "In Review", done: "Done" }
```

Other upgrade effects, each recorded in the LOG: an installation using the `recording` provider must set
`development: true` (LOG 557); a library naming a removed capability in `requires.capabilities` is refused at run start
(LOG 552); a template that guards on `declined_blocking` alone no longer sends a non-blocking demonstrated decline to a
person (LOG 555); `findings.triage`'s `complete` must be a literal (LOG 556).

## 6. Review

Two independent reviews, one per merge point.

- **Increment 1** (F17, F16, F13): no high, one medium, six low. The medium: a null `person_decides` or `complete`
  loaded, after which the engine required no classes while the API showed all three. Fixed by one reader for both
  inputs and by refusing a null or empty literal input at load (LOG 554, 556 amended). The lows (an empty list loading,
  a stale flag in `docs/06`, a stale comment, check 21 naming a `role_mapping` entry that does not exist, two gaps in
  the generated contract, a test-only overload in `src`) were fixed; a shared SQL builder was deferred.
- **Increments 2 and 3** (L4, F6, F15, L8): no high, two medium, four low. R2-1: a wrapper's `with` binding of a
  parameter its fragment reads as `params.<name>` (such as `engine_can_merge: true`) loaded and was ignored; it is now
  refused, through one binding helper shared with `use` (LOG 569). R2-2: runs in flight on a tracker without
  `status_map` are not checked again after the upgrade; the upgrade note orders the map first and hosts warn at start-up
  (LOG 570). The lows (pass-through regression test, a cascaded route error, stale markers, duplicated binding checks)
  were fixed.

## 7. Deviations and judgement calls

LOG 548 to 570, unreviewed. Worth a look: **LOG 563 and 570** (identity status default; accepted by the coordinator on
2026-11-24 on condition that a missing map fails loudly, which LOG 564 does at run start and LOG 570 warns of at
start-up), LOG 552 and 555 (an organisation library naming a removed capability, or guarding on `declined_blocking`
alone, changes), LOG 559 and 569 (the wrapper rule is a loader change chosen to keep every std step id). F15 was built
as an unprefixed wrapper rule rather than with today's prefixed `use`, which would have renamed every std step. The
shared SQL builder the first review suggested is deferred until a third SQL store exists.

## 8. Follow-ups

1. A distribution's agent image needs `ff-sandbox` for its architecture; no package carries it (R4, published images).
2. Publishing the packages to a feed, and versioning per release (R4 and the release process).
3. `FeatureFlow.Api` carries the console only when packed with Node; a distribution that wants the console packs with
   Node installed.
4. std is not packaged; a distribution uses a `directory` or `git` library source.
5. Console browser tests and `StuckRunTests` time out occasionally under heavy local load (several workers'
   containers); each passed alone. Worth watching on CI.

## 9. Open questions

1. LOG 563: accept identity as the tracker status default, with the upgrade step in section 5?
