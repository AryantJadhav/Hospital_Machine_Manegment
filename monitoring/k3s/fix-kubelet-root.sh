#!/usr/bin/env bash
# For a server where k3s is already installed with the kubelet's folder on the root disk (install-k3s.sh before 12 October
# 2026), and the root disk is nearly full, so the node reports "disk pressure" and starts no new pods.
# Moves the kubelet's working folder to /data and restarts k3s. Run ON THE SERVER:  sudo bash fix-kubelet-root.sh
# Pods restart (a few seconds, once). It changes nothing outside k3s.
set -euo pipefail
DATA_DIR="${DATA_DIR:-/data/k3s}"
[ "$(id -u)" -eq 0 ] || { echo "Run this with sudo."; exit 1; }
mkdir -p "$DATA_DIR/kubelet" /etc/rancher/k3s
cat > /etc/rancher/k3s/config.yaml <<CFG
kubelet-arg:
  - "root-dir=$DATA_DIR/kubelet"
CFG
systemctl restart k3s
echo "k3s restarted with the kubelet folder at $DATA_DIR/kubelet. Give it a minute, then: kubectl get nodes"
