#!/usr/bin/env bash
# Brings up Feature Flow in a local k3d cluster (the k3d profile, deploy/README.md): Temporal, Postgres and MinIO in the
# cluster, the three hosts and the sandbox from images built here, and the configuration in a folder you give:
#
#   deploy/k3d/up.sh <config folder>
#
# The folder holds installation.yaml, organisation.yaml, any file they name (all mounted in /etc/feature-flow), and
# optionally secrets.json (mounted as /var/run/secrets/feature-flow/secrets.json). Running it again rebuilds the images,
# applies changed configuration and restarts the hosts; the cluster, its data and its generated passwords are kept.
#
# The cluster's kubeconfig is written to its own file (FF_KUBECONFIG); your default kubeconfig and context are not
# touched. Settings, from the environment:
#   FF_K3D_CLUSTER  the cluster's name (default feature-flow)
#   FF_KUBECONFIG   where its kubeconfig goes (default ~/.config/feature-flow/<cluster>.kubeconfig)
#   FF_SKIP_BUILD   set to 1 to import the images already built rather than build them
#   K3D             the k3d command (default k3d)
#
# Run one up.sh per Docker daemon at a time: the images are tagged feature-flow-*:dev in the daemon, so two runs from
# different checkouts would overwrite each other's images before importing them.
#
# Generated secrets: created once, kept in the Secret feature-flow-infrastructure, and added to secrets.json for the
# installation to name: record_store_connection (a Postgres connection string), object_store_access_key and
# object_store_secret_key (MinIO's), and, only where the folder's secrets.json has none, step_token_key
# (agent_worker.token_key_ref), api_token (a local_token for the API) and payload_key (temporal.payload_codec's key). Nothing secret is printed; read the API token with
#   kubectl -n feature-flow get secret feature-flow-infrastructure -o jsonpath='{.data.api-token}' | base64 -d
set -euo pipefail

config=${1:?usage: up.sh <config folder>}
config=$(cd "$config" && pwd)
root=$(cd "$(dirname "$0")/../.." && pwd)
cluster=${FF_K3D_CLUSTER:-feature-flow}
export KUBECONFIG=${FF_KUBECONFIG:-$HOME/.config/feature-flow/$cluster.kubeconfig}
k3d=${K3D:-k3d}

for file in installation.yaml organisation.yaml; do
  [ -f "$config/$file" ] || { echo "up.sh: $config/$file is missing" >&2; exit 2; }
done

if ! "$k3d" cluster get "$cluster" >/dev/null 2>&1; then
  echo "Creating the k3d cluster $cluster"
  "$k3d" cluster create "$cluster" --no-lb --wait --timeout 180s \
    --kubeconfig-update-default=false --kubeconfig-switch-context=false \
    --k3s-arg "--disable=traefik@server:0"
fi
mkdir -p "$(dirname "$KUBECONFIG")"
"$k3d" kubeconfig get "$cluster" > "$KUBECONFIG"
chmod 600 "$KUBECONFIG"

# The three hosts' images and the sandbox image (src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile), tagged dev.
sandbox=$root/src/Providers/FeatureFlow.Providers.Docker
if [ "${FF_SKIP_BUILD:-}" != 1 ]; then
  for image in api:Api orchestration-worker:OrchestrationWorker agent-worker:AgentWorker; do
    echo "Building feature-flow-${image%%:*}:dev"
    docker build -q -f "$root/deploy/images/host.Dockerfile" --build-arg "HOST=${image#*:}" -t "feature-flow-${image%%:*}:dev" "$root" >/dev/null
  done
  echo "Building feature-flow-sandbox:dev"
  docker build -q -f "$sandbox/sandbox.Dockerfile" -t feature-flow-sandbox:dev "$sandbox" >/dev/null
fi
"$k3d" image import -c "$cluster" feature-flow-api:dev feature-flow-orchestration-worker:dev feature-flow-agent-worker:dev \
  feature-flow-sandbox:dev >/dev/null

rerun=$(kubectl -n feature-flow get deployment/api -o name 2>/dev/null || true)
kubectl apply -k "$root/deploy/k3d" >/dev/null

random() { head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'; }
infrastructure() { { kubectl -n feature-flow get secret feature-flow-infrastructure -o "jsonpath={.data.$1}" 2>/dev/null || true; } | base64 -d; }
# Each generated secret is made once and kept; a key an older up.sh did not make is added. Values go through stdin.
for key in postgres-password minio-password step-token-key api-token payload-key; do
  value=$(infrastructure "$key")
  if [ -z "$value" ]; then
    if [ "$key" = payload-key ]; then value=$(head -c 32 /dev/urandom | base64); else value=$(random); fi
  fi
  printf '%s=%s\n' "$key" "$value"
done | { cat; printf 'minio-user=feature-flow\n'; } |
  kubectl -n feature-flow create secret generic feature-flow-infrastructure --from-env-file=/dev/stdin --dry-run=client -o yaml |
  kubectl apply -f - >/dev/null

# secrets.json: the folder's own secrets, with the generated ones added.
for key in postgres-password minio-user minio-password step-token-key api-token payload-key; do infrastructure $key; echo; done |
  python3 -c '
import json, os, sys
postgres, user, password, step, api, payload = sys.stdin.read().split("\n")[:6]
path = os.path.join(sys.argv[1], "secrets.json")
secrets = json.load(open(path)) if os.path.exists(path) else {}
secrets.setdefault("step_token_key", step)
secrets.setdefault("api_token", api)
secrets.setdefault("payload_key", payload)
secrets.update({
    "record_store_connection": f"Host=postgres.feature-flow.svc;Database=feature_flow;Username=feature_flow;Password={postgres}",
    "object_store_access_key": user,
    "object_store_secret_key": password,
})
json.dump(secrets, sys.stdout)
' "$config" |
  kubectl -n feature-flow create secret generic feature-flow-secrets --from-file=secrets.json=/dev/stdin --dry-run=client -o yaml |
  kubectl apply -f - >/dev/null

files=()
for file in "$config"/*; do
  case "$(basename "$file")" in secrets*.json) ;; *) [ -f "$file" ] && files+=("--from-file=$file") ;; esac
done
kubectl -n feature-flow create configmap feature-flow-config "${files[@]}" --dry-run=client -o yaml | kubectl apply -f - >/dev/null

echo "Waiting for the services and the hosts"
[ -z "$rerun" ] || kubectl -n feature-flow rollout restart deployment/api deployment/orchestration-worker deployment/agent-worker >/dev/null
for deployment in temporal postgres minio api orchestration-worker agent-worker; do
  kubectl -n feature-flow rollout status "deployment/$deployment" --timeout 300s >/dev/null
done
kubectl -n feature-flow wait --for=condition=complete job/minio-bucket --timeout 300s >/dev/null

echo "Feature Flow is running in k3d cluster $cluster. To reach the API on http://localhost:8480:"
echo "  KUBECONFIG=$KUBECONFIG kubectl -n feature-flow port-forward svc/api 8480"
