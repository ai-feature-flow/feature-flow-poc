# Deploying Feature Flow

Three ways to run the engine, from one developer's machine to a team's cloud cluster. The code is the same in each;
only configuration differs (`docs/07` section 6, the profiles). The engine's parts are in `docs/02` section 1: the API,
the orchestration worker and the agent worker, with Temporal, a record store and an artifact store beside them.

| Folder | What it holds |
|---|---|
| `images/` | `host.Dockerfile`, the image of each of the three hosts |
| `kubernetes/` | The engine on any Kubernetes cluster: the hosts, their account, network policies |
| `k3d/` | A local cluster with Temporal, Postgres and MinIO in it: `up.sh`, `down.sh`, and `smoke/`, a trial configuration |
| `distribution-template/` | What a team copies to run Feature Flow: its configuration, deployment values and a README |

## Images

```sh
docker build -f deploy/images/host.Dockerfile --build-arg HOST=Api -t feature-flow-api .
docker build -f deploy/images/host.Dockerfile --build-arg HOST=OrchestrationWorker -t feature-flow-orchestration-worker .
docker build -f deploy/images/host.Dockerfile --build-arg HOST=AgentWorker -t feature-flow-agent-worker .
docker build -f src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile -t feature-flow-sandbox src/Providers/FeatureFlow.Providers.Docker
```

Each host image is the ASP.NET runtime with git (about 530 MB; the agent worker's is about 650 MB, with the sandbox
client it copies into each sandbox). It runs as an unprivileged user, holds no configuration or secret, and reads its
installation and organisation files from `/etc/feature-flow` (`FF_INSTALLATION`, `FF_ORGANISATION`). The API listens on
8480 (`FF_API_LISTEN`). On SIGTERM a host stops its workers and endpoints and exits 0, so Kubernetes can replace a pod
without waiting out its grace period. The standard library is in `/opt/feature-flow/library/std`.

## Solo developer

No cluster and no images: the three hosts run as local processes beside Temporal's dev server, with SQLite, the
filesystem store, the Docker sandbox and a local API token.

```sh
~/.temporalio/bin/temporal server start-dev --headless
dotnet run --project hosts/FeatureFlow.Host.OrchestrationWorker -- --installation installation.yaml --organisation organisation.yaml
# and the same for FeatureFlow.Host.AgentWorker and FeatureFlow.Host.Api
```

The installation names `sqlite`, `filesystem`, `file` secrets (with `from_environment` for a subscription token), the
`docker` sandbox with the sandbox image, and `api.auth.mode: local_token`. Stop a host with Ctrl+C or SIGTERM.

## k3d: a team's cluster on one machine

`deploy/k3d/up.sh <config folder>` creates a k3d cluster (default name `feature-flow`), builds and imports the images,
and deploys Temporal's dev server, Postgres and MinIO, each on its own volume, with the three hosts. Its kubeconfig goes
to its own file (`~/.config/feature-flow/<cluster>.kubeconfig`); your default context is not changed. Run it again to
apply changed configuration; `deploy/k3d/down.sh` deletes the cluster and its data.

| Config folder | Runs |
|---|---|
| `deploy/k3d/smoke` | Fake GitHub, tracker and CI, and a scripted agent: a std ticket run completes with no credentials. The `Cluster` tests use it |
| A copy of `distribution-template/config` | The small-company profile: Jira, GitHub, GitHub Actions, the Kubernetes sandbox, a real agent |

The folder's files become the ConfigMap `feature-flow-config`; its `secrets.json`, with the generated database and
MinIO passwords, API token and step-token key added, becomes the Secret `feature-flow-secrets`, which the installation's
`file` secrets provider reads. Nothing secret is printed. To use the API:

```sh
export KUBECONFIG=~/.config/feature-flow/feature-flow.kubeconfig
kubectl -n feature-flow port-forward svc/api 8480 &
kubectl -n feature-flow get secret feature-flow-infrastructure -o jsonpath='{.data.api-token}' | base64 -d > token
ff run start std:templates/ticket_delivery --param ticket_id=T-1 --api http://localhost:8480 --token-file token
```

Network policies (k3s enforces them): Temporal, Postgres and MinIO accept only the hosts; the agent worker's endpoint
accepts only sandboxes and hosts; sandboxes, in namespace `feature-flow-sandboxes`, reach only DNS, the agent worker and
the public internet. The hosts' account may manage pods only in the sandboxes' namespace.

## A cloud cluster on AWS

The same manifests (`deploy/kubernetes`) with AWS services in place of the k3d ones. This is configuration only; the
engine needs no change. Start from a copy of `distribution-template`.

| Part | On AWS | In `config/installation.yaml` |
|---|---|---|
| Cluster | EKS, with the VPC CNI's network policy support turned on (policies are ignored otherwise) | |
| Images | ECR | `images:` in `kustomization.yaml`; the sandbox's `image` |
| Temporal | Temporal's Helm chart in the cluster, on RDS | `temporal.address`, for example `temporal-frontend.temporal.svc:7233` |
| Record store | RDS for PostgreSQL | `record_store_connection` in the secrets: `Host=<endpoint>;Database=feature_flow;Username=...;Password=...;SSL Mode=Require` |
| Artifact store | An S3 bucket, versioned (`docs/02` section 5, recovery) | `bucket`, `region`; no `endpoint` and no keys: the hosts' service account takes an IAM role (EKS Pod Identity or IRSA) allowed to read and write the bucket |
| Secrets | AWS Secrets Manager, synced into the Secret `feature-flow-secrets` (for example by the External Secrets Operator) | unchanged: the `file` provider reads the synced `secrets.json` |
| API | A load balancer or ingress in front of `svc/api`, with TLS | `api.auth`: your identity provider's issuer and audience |

Temporal's frontend in the cluster should accept only the hosts (their pods carry `feature-flow/host: "true"`), as
`deploy/k3d/services.yaml` does for its services; RDS's security group should accept only the cluster. Sandboxes reach
neither: their policy excludes every private range. Temporal Cloud is not yet possible: the installation has no keys
for Temporal's TLS and API key.
