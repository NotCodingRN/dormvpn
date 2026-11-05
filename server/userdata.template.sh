#!/bin/bash
# DormVPN server bootstrap: sing-box VLESS + REALITY on TCP 443, BBR enabled,
# per-user usage meter that syncs users and limits from the DormVPN API. No SSH needed.
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
mkdir -p /etc/sing-box /etc/dormvpn /var/lib/dormvpn
chown root:singbox /etc/sing-box
chmod 0750 /etc/sing-box
chmod 0700 /etc/dormvpn /var/lib/dormvpn

# sing-box settings without users (the meter adds the users it gets from the API)
cat > /etc/dormvpn/singbox-base.json <<'EOF'
__SERVER_BASE__
EOF
# where the API is and the token that proves this is the real server
cat > /etc/dormvpn/dormvpn.json <<'EOF'
__METER_CONF__
EOF
chmod 0600 /etc/dormvpn/*.json

# --- per-user usage meter (syncs users, counts traffic with nftables, enforces limits)
dnf -y install nftables python3
echo '__METER_SCRIPT_B64__' | base64 -d | gunzip > /usr/local/bin/dormvpn-meter
chmod 0755 /usr/local/bin/dormvpn-meter

cat > /etc/systemd/system/sing-box.service <<'EOF'
[Unit]
Description=DormVPN (sing-box)
After=network-online.target
Wants=network-online.target

[Service]
User=singbox
Group=singbox
# NET_ADMIN lets sing-box mark each user's connections so the meter can count them
AmbientCapabilities=CAP_NET_BIND_SERVICE CAP_NET_ADMIN
CapabilityBoundingSet=CAP_NET_BIND_SERVICE CAP_NET_ADMIN
NoNewPrivileges=true
ExecStart=/usr/local/bin/sing-box run -c /etc/sing-box/config.json
Restart=always
RestartSec=3
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF
cat > /etc/systemd/system/dormvpn-meter-apply.service <<'EOF'
[Unit]
Description=DormVPN usage counters (rebuilt at boot)
Before=sing-box.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/dormvpn-meter apply
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF
cat > /etc/systemd/system/dormvpn-meter.service <<'EOF'
[Unit]
Description=DormVPN usage meter tick
After=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/dormvpn-meter tick
EOF
cat > /etc/systemd/system/dormvpn-meter.timer <<'EOF'
[Unit]
Description=DormVPN usage meter, every minute

[Timer]
OnBootSec=20s
OnUnitActiveSec=1min
AccuracySec=5s

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
# first tick writes the sing-box config with the current users and starts it
/usr/local/bin/dormvpn-meter tick
systemctl enable sing-box dormvpn-meter-apply.service
systemctl enable --now dormvpn-meter.timer
systemctl is-active sing-box

# --- automatic security updates
dnf -y install dnf-automatic || true
sed -i 's/^apply_updates.*/apply_updates = yes/' /etc/dnf/automatic.conf 2>/dev/null || true
systemctl enable --now dnf-automatic.timer || true

echo "DORMVPN_SETUP_DONE"
