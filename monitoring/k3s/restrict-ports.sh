#!/usr/bin/env bash
# Closes the three ports k3s and the node exporter open on every network interface of the server, to everything except the
# server itself and the cluster's own pods. Run ON THE SERVER, as root, once:  sudo bash restrict-ports.sh
#
#   9100   the node exporter: the server's CPU, memory, disk and network figures, with no password
#   6443   the cluster's API (controlling the cluster needs a certificate, but there is no reason to offer it to a network)
#   10250  the kubelet's API
#
# Found on 12 October 2026: all three answered from another computer on the Tailscale network. Prometheus (a pod) and the
# cluster itself still reach them: the first two rules let the pod and service networks through, and the server's own
# traffic to itself (kubectl, the kubelet) never passes the firewall's interface rules. ufw checks rules in order and the
# first match wins, so these are inserted at the top. Undo with:  sudo ufw status numbered   then   sudo ufw delete <number>
set -euo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run this with sudo."; exit 1; }
command -v ufw >/dev/null || { echo "ufw is not installed; close 9100, 6443 and 10250 with your own firewall."; exit 1; }

ufw insert 1 allow from 10.42.0.0/16 to any port 9100,6443,10250 proto tcp comment 'k3s pods to node ports'
ufw insert 2 allow from 10.43.0.0/16 to any port 6443 proto tcp comment 'k3s services to the API'
ufw insert 3 deny in to any port 9100,6443,10250 proto tcp comment 'not for the network'
ufw status numbered | head -12
