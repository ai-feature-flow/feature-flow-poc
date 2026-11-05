# M8 results: team profile and operations

## Foundations

What M8's parallel builders start from, built on `m8-foundations` (W0) before them: the backward-compatibility code
removed, the organisation keys not in version 1 refused, and the shared files the builders would otherwise all edit
prepared. No provider implementations.

**What was built**

| Area | Change |
|---|---|
| No backward compatibility (LOG 263) | All 21 Temporal patch markers in `RunWorkflow*.cs` deleted with the old-history branches they guarded; today's behaviour kept. The interpreter's `RunOptions.BranchFromPattern` and `Steering`, which the shell set from markers, deleted with their old paths: the pre-M7 skip refusal (LOG 207), the pre-M7 `auto_resolved` text (LOG 258), and the pre-M7 patch and go-to checks. `artifact.validate`'s path for a scope without consumed artifacts deleted (LOG 272). `project_session_pre_m7` (LOG 240's pre-session path) and the tests of old paths deleted |
| Histories | Every other history broke on replay (a marker is a recorded command) and was re-recorded from the integration suite with `FF_RECORD_HISTORIES`. The marker-presence tests became one table: each history holds the activity, signal, update or recorded text of the path it covers (LOG 271) |
| Organisation keys (LOG 268 to 270) | `library_overrides`, `library_inputs`, `timeouts` and `policy.toolsets` refused whole: "`<key>` is not supported in version 1; remove it". Their parsing and model fields removed (LOG 273). `role_mapping` stays refused until the Agents builder lifts it with its consumer (LOG 267) |
| Store contract suites | `RecordStoreContract` (runs, waits, timeline, answers, locks, agent step records) and `ArtifactStoreContract` (content addressing, round trips, `not_found`, signed URLs), subclassed for SQLite and the filesystem store (LOG 274) |
| Provider scaffolding | New `FeatureFlow.Providers.S3` and its tests project. Postgres, S3, Kubernetes, CopilotCli and Jira each have an empty `Factories` list, registered by all three hosts (LOG 275) |

**Checks**

| Check | Shows |
|---|---|
| Replay of 19 re-recorded histories | Today's workflow replays every recorded path |
| Each history takes the path it covers (14 rows) | Every covered path has a history recorded on it, so a change to that path's commands (activities, signals, updates, children) fails a replay. A `text` row proves only that the history took the path: replay compares commands, not activity inputs, so a change confined to an input or a record's text is caught by the unit and integration tests instead |
| Each key not in version 1 is refused alone, whatever it holds | The four refusals, with malformed content still refused only for the key |
| `RecordStoreContract`, `ArtifactStoreContract` on SQLite and the filesystem | The solo profile's stores meet the suites the Postgres and S3 providers must pass |
| Architecture test | The S3 project is in the reference rule table and obeys it |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| Replay | The workflow skips the `clean_up` activity: `ticket_clean_up` fails with a nondeterminism error |
| Path table | Run against the old `family_parent` (recorded before `EnsureWorkBranches`): its row fails |
| Key refusal | The refusal's error removed: the test fails |
| Record store suite | Timeline `INSERT OR REPLACE`; a late "open" replaces an answered question; runs listed oldest first: the three matching tests fail |
| Artifact store suite | A put truncated at 1 MB; a signed URL to the store's root: the two matching tests fail |
| Architecture test | S3 dropped from the rule table, and Jira pointed at `Engine`: it fails naming both |

**Deviations and judgement calls:** LOG 271 to 275.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | No sandbox or agent runtime contract suite exists; the Docker sandbox is covered only by the agent integration tests. The Agents builder writes `SandboxContract` (with the network checks) once, for Docker and Kubernetes |
| 2 | `docs/02` section 8's provider decorator (spans, timeouts, classification) does not exist; the Operations builder adds it in `ProviderComposer` |
| 3 | Library sources are resolved to a directory in three places (`ApiApp.Runs`, `ApiApp.Decisions`, `StartupChecks`); the `git` source needs one resolver used by all three |
| 4 | `ChildRunTests`' artifact-index order raced once in the full integration runs (it passed 5 of 5 alone). The workflow starts a child before it records the step that produced the parent's artifact, so under load the child's records can land first. New runs took the same order before M8. Fix in the workflow (record before starting children, re-recording the histories that breaks), not by loosening the assertion |

## Packaging

Built on `m8-packaging` (W4): the three hosts as images, the k3d deployment, a template a team copies, deployment notes,
and CI for every test type of `docs/09` section 4.2. The M6 epic on the cluster (done-when 1) is the integration step's;
this makes it possible.

**What was built**

| Area | Change |
|---|---|
| Clean stop (M6 follow-up 7) | A host handles SIGTERM and SIGINT itself, stops its workers and endpoints, logs `Stopped` and exits 0. ASP.NET's handler had taken SIGTERM in the API and the agent worker and stopped only the endpoint, so they never exited (LOG 312) |
| Images | `deploy/images/host.Dockerfile`, one for the three hosts (`HOST`): ASP.NET runtime, git, tini as process 1, the image's unprivileged user, std in `/opt/feature-flow/library/std`, the sandbox client in the agent worker's. No configuration or secret baked in (LOG 311) |
| Engine manifests | `deploy/kubernetes`: the three deployments (read-only root, no privilege), services, one account with rights only in the sandboxes' namespace, network policies: ingress denied by default, sandboxes reach only DNS, the agent worker and the public internet (LOG 314) |
| k3d profile | `deploy/k3d`: Temporal's dev server, Postgres and MinIO in the cluster, each on a volume and reachable only from the hosts (LOG 315). `up.sh <config folder>` creates the cluster with its own kubeconfig file, builds and imports the images, generates the infrastructure secrets once and makes the ConfigMap and Secret (LOG 313); `down.sh` deletes it. `smoke/` is a trial configuration with fake providers outside the cluster (LOG 317) |
| Distribution template | `deploy/distribution-template`: the small-company profile's installation and organisation, an example secrets file, a Kustomize overlay for a team's own cluster, a README |
| Deployment notes | `deploy/README.md`: the solo developer, k3d, and AWS (EKS, RDS, S3, Temporal's Helm chart) as configuration |
| CI | A `Cluster` category and job: k3d, `up.sh deploy/k3d/smoke`, the `Cluster` tests. `ci.yml` names where each test type runs (LOG 316) |

Where each test type of `docs/09` section 4.2 runs (done-when 2):

| Type | CI job (tests) |
|---|---|
| Validation, re-drive, readable form | Default (`Validation.Tests`, `Testing.Workflows.Tests` over `library/std/tests`, `ReadableFormTests` golden files) |
| Replay | Default (`Replay.Tests`) |
| Failure injection | Integration (`ContinueAsNewTests`, `AgentResumeTests`, `Sp1*`, `StepFailureTests`, `HostProcessTests`) |
| Run-time rules | Default (`Engine.Tests`) and integration (`PolicyRunTests`, `StepStartCheckTests`) |
| Provider contracts | Default (in-process providers), integration (Docker; Postgres and MinIO in containers), cluster (the Kubernetes sandbox) |
| Merge race | Integration (`MergeRaceTests`, `EngineMergeRaceTests`) |
| Agent reporting | Integration (`AgentContractTests`) |
| Pre-submit parity | Integration (`SessionRunTests`) |
| The deployment itself | Cluster (`Cluster/DeploymentTests`) |

**Checks**

| Check | Shows |
|---|---|
| `HostProcessTests` (3) | Each built host, as a process, exits 0 within 20 s of SIGTERM, having logged `Stopped` |
| `DeploymentTests`: a sandbox's probe | A pod in the sandboxes' namespace reaches the agent worker's endpoint and the internet, and not Temporal, Postgres, MinIO or the API |
| `DeploymentTests`: hosts only | A pod labelled as a host reaches all six targets (the method sees what is open); a plain pod in the engine's namespace reaches only the API and the internet |
| `DeploymentTests`: clean stop (3) | Deleting each host's pod takes under 20 s (the grace period is 30) and its log ends `Stopped` |

**Controls** (each broke the guarded code, ran the check, saw it fail, and restored it)

| Check | Mutation |
|---|---|
| `HostProcessTests` | The old `Console.CancelKeyPress` handler: all three fail (the API and agent worker hang; the orchestration worker exits without stopping) |
| Sandbox probe | The sandboxes' policy deleted: the sandbox reaches the API, Temporal and the stores |
| Hosts only | The services' policy opened to every pod: a plain pod reaches Temporal, Postgres and MinIO |
| Clean stop | The API image built without the fix: no `Stopped`, and the pod waits out its grace period |
| The account's rights | The role without reading network policies: the API crash-loops (the Kubernetes sandbox's startup check) |
| tini | Without it, a host that fails at startup spins at full CPU forever as process 1; with it, it exits 134 |

**Deviations and judgement calls:** LOG 311 to 318.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | A new pod's first connection to Temporal can be refused while k3s applies network policies to it; the host exits and Kubernetes restarts it once. A host could retry its first connection instead (docs/02 section 7) |
| 2 | Temporal Cloud needs `temporal.tls` and `temporal.auth` in the installation (docs/07 section 2 shows them; nothing reads them) |
| 3 | No image is published: a team builds the images at a release and pushes them to its registry |
| 4 | Once the Stores and Agents builders' work is in `m8`: the smoke configuration moves to Postgres, MinIO and the Kubernetes sandbox, and `ClusterRunTests` (a std ticket run through the API, completing across the three pods) joins the `Cluster` tests. Both passed on a local scratch merge of the three branches; until then each smoke host keeps its own SQLite file, so the cluster tests check the deployment but run nothing |
