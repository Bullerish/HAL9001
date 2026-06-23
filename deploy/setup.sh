#!/usr/bin/env bash
# HAL9001 server setup. Run as root ON THE BOX, from this deploy/ directory, AFTER you've copied a
# self-contained publish to /opt/hal9001 (see deploy/README.md). Idempotent — safe to re-run.
set -euo pipefail

APP=/opt/hal9001
ENVDIR=/etc/hal9001

echo "==> service user 'hal'"
id hal &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin hal

echo "==> permissions on $APP"
[ -f "$APP/HAL9001" ] || { echo "ERROR: $APP/HAL9001 not found — copy the publish output there first."; exit 1; }
chown -R hal:hal "$APP"
chmod +x "$APP/HAL9001"

echo "==> env file $ENVDIR/hal.env"
install -d -m 700 "$ENVDIR"
if [ ! -f "$ENVDIR/hal.env" ]; then
  install -m 600 "$(dirname "$0")/hal.env.example" "$ENVDIR/hal.env"
  echo "    created from template — EDIT $ENVDIR/hal.env with your real secrets before the hive can think."
fi

echo "==> systemd services"
install -m 644 "$(dirname "$0")/hal-swarm.service" "$(dirname "$0")/hal-dashboard.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable hal-swarm hal-dashboard

echo
echo "Next:"
echo "  1) edit $ENVDIR/hal.env  (ANTHROPIC_API_KEY, TURSO_*, optional HAL_DONATE_SECRET)"
echo "  2) systemctl restart hal-swarm hal-dashboard"
echo "  3) install Caddy + the Caddyfile (see deploy/README.md) for TLS at hal9001.io"
echo "  4) logs:  journalctl -u hal-dashboard -f"
