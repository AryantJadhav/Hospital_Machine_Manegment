#!/usr/bin/env bash
# Run on the k3s server as root:   sudo bash reset-containerd.sh
#
# For when pods stay in ContainerCreating with "blob sha256:... expected at .../io.containerd.content.v1.content/...:
# blob not found". The image store of k3s's own containerd has lost files (a full disk, or a clean-up on a shared server),
# so it believes it has images that it no longer has. This empties that store; every image is pulled again on the next start.
#
# What it touches:   /data/k3s/agent/containerd   (the image store only)
# What it does NOT touch: /data/k3s/storage (the volumes: the database and the application's data), /data/k3s/server (the
# cluster's own records), and Docker (/data/docker), which is a different engine with its own images.
#
# The server must be able to reach the internet to pull the images again (docker.io, quay.io, registry.k8s.io, ghcr.io).
set -euo pipefail

STORE=/data/k3s/agent/containerd

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this with sudo." >&2
  exit 1
fi

case "$STORE" in
  /data/k3s/agent/containerd) ;;
  *) echo "Refusing: unexpected path $STORE" >&2; exit 1 ;;
esac

if [ ! -d "$STORE" ]; then
  echo "$STORE does not exist: nothing to reset." >&2
  exit 1
fi

echo "Stopping k3s and its pods..."
systemctl stop k3s
if [ -x /usr/local/bin/k3s-killall.sh ]; then
  /usr/local/bin/k3s-killall.sh || true
fi

echo "Emptying the image store: $STORE"
rm -rf "${STORE:?}"/*

echo "Starting k3s..."
systemctl start k3s

echo "Done. Wait a few minutes for the images to be pulled again, then:"
echo "  sudo install -o Aryant050 -g Aryant050 -m 600 /etc/rancher/k3s/k3s.yaml ~/.kube/config"
echo "  kubectl get pods -A"
