#!/bin/bash
# DormVPN server bootstrap: sing-box VLESS + REALITY on TCP 443, BBR enabled.
set -euo pipefail
exec > /var/log/dormvpn-setup.log 2>&1

# --- TCP tuning: BBR keeps throughput up on lossy routes (2-6 Mbps -> ~26 Mbps in testing)
cat > /etc/sysctl.d/99-dormvpn.conf <<'EOF'
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr
net.ipv4.tcp_rmem = 4096 131072 33554432
net.ipv4.tcp_wmem = 4096 65536 33554432
net.core.rmem_max = 33554432
net.core.wmem_max = 33554432
net.ipv4.tcp_fastopen = 3
net.ipv4.tcp_mtu_probing = 1
net.ipv4.tcp_slow_start_after_idle = 0
EOF
modprobe tcp_bbr || true
sysctl --system

# --- sing-box (pinned version, verified checksum)
cd /tmp
curl -fsSL --retry 5 -o sb.tgz "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-linux-amd64.tar.gz"
echo "699a1b510aa4396b2176d32d1181920399ebf1e267835da6f3b598d0f7c24968  sb.tgz" | sha256sum -c -
tar xzf sb.tgz
install -m 0755 sing-box-1.14.4-linux-amd64/sing-box /usr/local/bin/sing-box

useradd --system --no-create-home --shell /sbin/nologin singbox || true
mkdir -p /etc/sing-box
cat > /etc/sing-box/config.json <<'EOF'
__SERVER_CONFIG__
EOF
chown -R root:singbox /etc/sing-box
chmod 0750 /etc/sing-box
chmod 0640 /etc/sing-box/config.json
/usr/local/bin/sing-box check -c /etc/sing-box/config.json

cat > /etc/systemd/system/sing-box.service <<'EOF'
[Unit]
Description=DormVPN (sing-box)
After=network-online.target
Wants=network-online.target

[Service]
User=singbox
Group=singbox
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ExecStart=/usr/local/bin/sing-box run -c /etc/sing-box/config.json
Restart=always
RestartSec=3
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now sing-box

# --- automatic security updates
dnf -y install dnf-automatic || true
sed -i 's/^apply_updates.*/apply_updates = yes/' /etc/dnf/automatic.conf 2>/dev/null || true
systemctl enable --now dnf-automatic.timer || true

echo "DORMVPN_SETUP_DONE"
