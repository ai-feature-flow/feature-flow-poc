#!/usr/bin/env bash
# Deletes the k3d cluster up.sh created, with its data, and its kubeconfig file.
set -euo pipefail
cluster=${FF_K3D_CLUSTER:-feature-flow}
"${K3D:-k3d}" cluster delete "$cluster"
rm -f "${FF_KUBECONFIG:-$HOME/.config/feature-flow/$cluster.kubeconfig}"
