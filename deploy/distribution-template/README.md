# Feature Flow distribution

This folder is a template. Copy it into a repository of your own to run Feature Flow for your team: it holds your
configuration and how you deploy it, and nothing of Feature Flow's code. Deployment in more detail, including a solo
developer's setup, is in Feature Flow's `deploy/README.md`.

| File | What it is |
|---|---|
| `config/installation.yaml` | Your installation (Feature Flow's `docs/07` section 2): Temporal, the stores, the sandbox, the agent runtime, GitHub, Jira, sign-in |
| `config/organisation.yaml` | Your organisation (`docs/07` section 3): libraries, who may approve, people roles from your identity provider's groups |
| `config/secrets.example.json` | The secrets the installation names. Copy it to `config/secrets.json` (ignored by git) or keep them in your secret manager |
| `kustomization.yaml` | Deploys the engine to your own cluster with your configuration and images |

Values marked `yours` in the two configuration files must be replaced.

## Your own providers

A provider of your own needs no fork of Feature Flow. Build your host from Feature Flow's packages: a project that
references `FeatureFlow.Hosting` and `FeatureFlow.Providers.Standard`, composes `StandardProviders.Factories` with
your own factories, and calls `HostArguments.RunAsync` as Feature Flow's hosts do (Feature Flow's `docs/06` section 4).
Feature Flow's `samples/distribution` is a working example, with a validator of its own and a workflow test that uses
it; its `README.md` shows how to pack the engine to a local folder and build against it. Feature Flow does not yet ship
a Dockerfile for a distribution's host: build its image as Feature Flow's `deploy/images/host.Dockerfile` builds the
engine's hosts.

## Try it on your machine (k3d)

With Docker, [k3d](https://k3d.io), `kubectl` and a checkout of Feature Flow:

```sh
cp config/secrets.example.json config/secrets.json    # then fill in model_key, github_app_key and jira
<feature-flow>/deploy/k3d/up.sh config
```

`up.sh` creates a cluster with Temporal, Postgres and MinIO in it, builds the images, and adds the generated secrets
(the database and MinIO passwords, the API token and the step-token key) to yours.

## Run it on your cluster

1. **Images.** Build the three host images and the sandbox image at the Feature Flow release you run (see
   `deploy/README.md`) and push them to your registry. Set `images:` in `kustomization.yaml` and the sandbox's `image`
   in `config/installation.yaml`.
2. **Services.** Temporal, Postgres and an S3 bucket, reachable from the cluster. Set their addresses in
   `config/installation.yaml`; the changes for AWS are in `deploy/README.md`.
3. **Secrets.** Create the Secret `feature-flow-secrets` in namespace `feature-flow`, with one key `secrets.json`
   holding the values `config/secrets.example.json` names, from your secret manager.
4. **Deploy.** `kubectl apply -k .`

The cluster's network plugin must enforce network policies: they keep sandboxes away from Temporal, the stores and
the API.
