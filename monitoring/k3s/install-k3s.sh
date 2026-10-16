#!/usr/bin/env bash
# Installs k3s (a small, single-binary Kubernetes) on the Linux server, for the monitoring stack and for a Kubernetes
# copy of Hospital PM to test against. Run it ON THE SERVER, as root, once:
#
#     sudo bash install-k3s.sh
#
# It does NOT touch Docker or anything Docker runs (Hospital PM's Compose copy and the other applications on this server keep
# running as they are). It adds one service, "k3s", and one set of rules to the firewall.
#
# Choices made for THIS server, and why:
#   --data-dir /data/k3s         everything k3s stores (images, volumes, its database) goes on /data (1.8 TB), never the root
#                                disk, which was 99% full when this was written
#   --disable traefik            k3s's bundled web proxy would take ports 80 and 443, which the existing Caddy already holds
#   --disable servicelb          k3s's bundled load balancer binds host ports; nothing needs it, and it can clash with Docker
#   --write-kubeconfig-mode 600  only root and the account named below can use the cluster
# The version is pinned (the "stable" channel on 12 October 2026), not "latest".
set -euo pipefail

K3S_VERSION="${K3S_VERSION:-v1.36.5+k3s1}"
DATA_DIR="${DATA_DIR:-/data/k3s}"
ADMIN_USER="${ADMIN_USER:-${SUDO_USER:-}}"

[ "$(id -u)" -eq 0 ] || { echo "Run this with sudo."; exit 1; }
[ -n "$ADMIN_USER" ] && id "$ADMIN_USER" >/dev/null 2>&1 || { echo "Set ADMIN_USER=<the login that will run kubectl>."; exit 1; }

mkdir -p "$DATA_DIR" "$DATA_DIR/kubelet"

# The kubelet (k3s's node agent) keeps its working files in /var/lib/kubelet, on the ROOT disk, and judges "disk pressure"
# by how full that disk is. On this shared server the root disk is 99% full (other people's files), so Kubernetes would
# refuse to start any pod. Keeping the kubelet's folder on /data, with the rest, avoids that. Found on 12 October 2026.
mkdir -p /etc/rancher/k3s
cat > /etc/rancher/k3s/config.yaml <<CFG
kubelet-arg:
  - "root-dir=$DATA_DIR/kubelet"
CFG

# Four things the firewall (ufw is active here) must allow, or pods cannot talk to each other or to the cluster's API: the two
# internal networks k3s makes, and its two network interfaces. Nothing here opens the server to the outside: port 6443 (the
# cluster's API) is NOT opened, so it stays reachable only from the server itself.
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  ufw allow from 10.42.0.0/16 to any comment 'k3s pods'
  ufw allow from 10.43.0.0/16 to any comment 'k3s services'
  ufw allow in on cni0 comment 'k3s'
  ufw allow in on flannel.1 comment 'k3s'
fi

curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="$K3S_VERSION" sh -s - server \
  --data-dir "$DATA_DIR" \
  --default-local-storage-path "$DATA_DIR/storage" \
  --disable traefik \
  --disable servicelb \
  --write-kubeconfig-mode 600 \
  --node-name hospitalpm-test

# Let the named login use kubectl without sudo.
home="$(getent passwd "$ADMIN_USER" | cut -d: -f6)"
install -d -m 700 -o "$ADMIN_USER" -g "$(id -gn "$ADMIN_USER")" "$home/.kube"
install -m 600 -o "$ADMIN_USER" -g "$(id -gn "$ADMIN_USER")" /etc/rancher/k3s/k3s.yaml "$home/.kube/config"

echo
echo "k3s $K3S_VERSION is installed. Data is in $DATA_DIR. kubectl is ready for $ADMIN_USER:"
echo "    kubectl get nodes"
