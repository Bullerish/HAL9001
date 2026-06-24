#!/usr/bin/env bash
# HAL9001 box hardening — run as root on the server (Ubuntu 24.04; e.g. an IONOS VPS / Cloud Server).
#
# The public origin IP is published via DNS (hal9001.io), so it is NOT a secret — the real protection
# is at the host/network layer. This script applies the safe, CI-compatible baseline:
#   1) firewall (ufw)          — only 22/80/443 inbound
#   2) fail2ban                — ban brute-forcers hammering SSH
#   3) unattended-upgrades     — auto-install security patches
#   4) SSH key-only            — OPT-IN (HARDEN_SSH=1), with a lockout guard
#
# It is IDEMPOTENT and CONSERVATIVE — by design it will NOT lock you out:
#   • SSH is allowed through the firewall BEFORE the firewall is enabled.
#   • Password auth is only disabled when you pass HARDEN_SSH=1 AND a key is already present for root.
#
# Usage:
#   sudo bash harden.sh                 # steps 1-3 (safe; leaves SSH auth untouched)
#   sudo HARDEN_SSH=1 bash harden.sh    # also do step 4 (key-only SSH) — see the warning it prints
#   sudo SSH_PORT=2222 bash harden.sh   # if you run SSH on a non-default port
#
set -euo pipefail

SSH_PORT="${SSH_PORT:-22}"
HARDEN_SSH="${HARDEN_SSH:-0}"

[ "$(id -u)" -eq 0 ] || { echo "Run as root:  sudo bash harden.sh"; exit 1; }

echo "== HAL9001 box hardening (SSH_PORT=${SSH_PORT}, HARDEN_SSH=${HARDEN_SSH}) =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y ufw fail2ban unattended-upgrades

# ── 1. Firewall ───────────────────────────────────────────────────────────────────────────────────
# Open SSH/HTTP/HTTPS FIRST, then enable — opening before enabling is what prevents a self-lockout.
echo "-- [1/4] firewall (ufw): allow ${SSH_PORT}/tcp, 80/tcp, 443/tcp --"
ufw allow "${SSH_PORT}/tcp" comment 'SSH'  >/dev/null
ufw allow 80/tcp            comment 'HTTP (Caddy / ACME challenge)' >/dev/null
ufw allow 443/tcp           comment 'HTTPS (Caddy)' >/dev/null
ufw --force enable
ufw status verbose

# ── 2. fail2ban ───────────────────────────────────────────────────────────────────────────────────
echo "-- [2/4] fail2ban: ban SSH brute-force --"
cat > /etc/fail2ban/jail.d/hal.conf <<EOF
[sshd]
enabled  = true
port     = ${SSH_PORT}
maxretry = 4
findtime = 10m
bantime  = 1h
EOF
systemctl enable --now fail2ban
systemctl restart fail2ban
fail2ban-client status sshd || true

# ── 3. Automatic security updates ───────────────────────────────────────────────────────────────────
echo "-- [3/4] unattended-upgrades: auto security patches --"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
systemctl enable --now unattended-upgrades || true

# ── 4. SSH key-only (OPT-IN) ────────────────────────────────────────────────────────────────────────
if [ "${HARDEN_SSH}" != "1" ]; then
  echo "-- [4/4] SSH key-only: SKIPPED (set HARDEN_SSH=1 to enable) --"
  echo "   Before enabling: make sure YOUR OWN public key is in /root/.ssh/authorized_keys and that you"
  echo "   can log in with it from a SECOND terminal. Then re-run:  sudo HARDEN_SSH=1 bash harden.sh"
  echo "== done (steps 1-3) =="
  exit 0
fi

echo "-- [4/4] SSH key-only --"
# LOCKOUT GUARD: refuse to disable passwords unless root already has at least one authorized key.
if [ ! -s /root/.ssh/authorized_keys ]; then
  echo "!! ABORT: /root/.ssh/authorized_keys is empty/missing — disabling passwords now would lock you out."
  echo "!! Add your public key first:  ssh-copy-id root@<box>   (or paste it into that file), then re-run."
  exit 1
fi
echo "   Found $(grep -c . /root/.ssh/authorized_keys) authorized key line(s) for root."
echo "   !! KEEP THIS SESSION OPEN and confirm key login in a SECOND terminal before logging out."

# Neutralize any conflicting directives wherever they live (sshd uses FIRST match, and cloud images
# often ship /etc/ssh/sshd_config.d/50-cloud-init.conf with 'PasswordAuthentication yes'), then put
# our values in a single drop-in so they win regardless of file ordering.
for f in /etc/ssh/sshd_config /etc/ssh/sshd_config.d/50-cloud-init.conf; do
  [ -f "$f" ] || continue
  cp -n "$f" "$f.hal-bak" 2>/dev/null || true
  sed -ri 's/^\s*#?\s*(PasswordAuthentication|PermitRootLogin|KbdInteractiveAuthentication|ChallengeResponseAuthentication)\b.*/# \1 -> set by 99-hal-harden.conf/I' "$f"
done

cat > /etc/ssh/sshd_config.d/99-hal-harden.conf <<'EOF'
# HAL9001 SSH hardening (managed by deploy/harden.sh)
PermitRootLogin prohibit-password
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF

# Validate BEFORE reloading; reload (not restart) so the current session is not dropped.
if sshd -t; then
  systemctl reload ssh || systemctl reload sshd
  echo "   SSH is now key-only. Effective settings:"
  sshd -T | grep -Ei '^(passwordauthentication|permitrootlogin|kbdinteractiveauthentication)\b' || true
  echo "   To revert: remove /etc/ssh/sshd_config.d/99-hal-harden.conf and 'systemctl reload ssh'."
else
  echo "!! sshd config test FAILED — NOT reloading. Restoring backups."
  for f in /etc/ssh/sshd_config /etc/ssh/sshd_config.d/50-cloud-init.conf; do
    [ -f "$f.hal-bak" ] && mv "$f.hal-bak" "$f"
  done
  rm -f /etc/ssh/sshd_config.d/99-hal-harden.conf
  exit 1
fi

echo "== done (steps 1-4) =="
