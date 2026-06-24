# Deploying HAL9001 live at hal9001.io

Goal: a cheap always-on box serving the HAL 9001 dashboard at `https://hal9001.io`, with the hive
thinking in the background. The files in this folder make it repeatable.

Architecture: two processes share the same Turso hive — `swarm` (thinks: races, journals, answers
asks) and `dashboard` (serves the read-only web UI on localhost:8765). **Caddy** fronts the
dashboard with automatic TLS. Secrets live only in `/etc/hal9001/hal.env` (never in git).

---

## 1. Get a box + point the domain at it

- Provision a small Linux VPS (Ubuntu 24.04; Hetzner CX22 ~€4/mo or DigitalOcean $6/mo is plenty).
- Note its public IP. In your DNS (where hal9001.io is registered), add **A records**:
  - `hal9001.io` → `<box IP>`
  - `www.hal9001.io` → `<box IP>`
- Open the firewall: `ufw allow 22 && ufw allow 80 && ufw allow 443 && ufw enable`

## 2. Build a self-contained publish (on YOUR machine) and copy it up

Self-contained = the .NET runtime is bundled, so the box needs nothing installed.

```bash
# on your dev machine, in the repo root:
dotnet publish -c Release -r linux-x64 --self-contained -o publish-linux

# copy the app + these deploy files to the box:
ssh root@<box>  "mkdir -p /opt/hal9001"
scp -r publish-linux/*  root@<box>:/opt/hal9001/
scp -r deploy           root@<box>:/opt/hal9001/
```

## 3. Run the setup script (on the box, as root)

```bash
ssh root@<box>
cd /opt/hal9001/deploy
bash setup.sh
```

This creates the `hal` service user, installs `/etc/hal9001/hal.env` from the template, and installs
+ enables the two systemd services.

## 4. Add your secrets, then start the hive

```bash
nano /etc/hal9001/hal.env      # ANTHROPIC_API_KEY, TURSO_*, optional HAL_DONATE_SECRET
systemctl restart hal-swarm hal-dashboard
journalctl -u hal-swarm -f     # watch it come alive
```

The dashboard is now live on `localhost:8765` (not yet public — Caddy is next).

## 5. TLS + the domain (Caddy)

```bash
# install Caddy (official apt repo):
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudflare.com/cdn-cgi/scripts/caddy/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudflare.com/cdn-cgi/scripts/caddy/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list
apt update && apt install -y caddy

# use our Caddyfile and reload:
cp /opt/hal9001/deploy/Caddyfile /etc/caddy/Caddyfile
systemctl reload caddy
```

Caddy fetches a Let's Encrypt cert automatically (needs the DNS A records from step 1 and ports
80/443 open). Within a minute, **https://hal9001.io** is live.

## 6. Verify

```bash
curl -s https://hal9001.io/api/state | head -c 300     # JSON from the hive
```
Open `https://hal9001.io` in a browser — the red eye, the race, the live feed. Click ♪ to hear it.

## 7. Harden the box (recommended once live — the IP is public via DNS)

`hal9001.io` resolves to the origin IP, so it's discoverable — protect at the host layer, not by
hiding it. Run the included script (idempotent; won't lock you out):

```bash
cd /opt/hal9001/deploy
sudo bash harden.sh                  # firewall (ufw 22/80/443) + fail2ban + auto security updates
# then, AFTER confirming your own SSH key works from a 2nd terminal:
sudo HARDEN_SSH=1 bash harden.sh     # disable password login (key-only; keeps CI's key-based root)
```

`HARDEN_SSH=1` sets `PermitRootLogin prohibit-password` so the GitHub Actions deploy (key-based root)
keeps working while password logins are turned off. It refuses to run unless `/root/.ssh/authorized_keys`
already has a key, so add yours first (`ssh-copy-id root@<box>`) and keep a session open while you test.

**IONOS specifics:** a VPS / Cloud Server gives you full root (confirm with `ssh root@<box>` → `whoami`).
IONOS Cloud Servers also have a **panel-level firewall** in the Cloud Panel that sits *in front of* the
box independently of `ufw` — if it's enabled there, you must also allow **22, 80, 443** in the panel or
the site/SSH won't be reachable even after `ufw` is correct. (Plain VPS plans usually rely on `ufw` only.)

**Optional, stronger:** put the origin behind a CDN/proxy (e.g. Cloudflare, free tier) so the public
IP becomes the proxy's and you can firewall the origin to the proxy's ranges — the only thing that
actually *hides* the origin IP.

---

## Notes

- **Cost.** `HAL_PACE=slow` keeps token burn low. Run the hive in **one** place (stop your local
  swarm) so you're not paying for two. There is **no hard daily spend cap yet** — watch your
  Anthropic usage until that's built.
- **Donations.** Set `HAL_DONATE_SECRET` to enable `/api/donate`; point your Stripe webhook at
  `https://hal9001.io/api/donate` with header `X-HAL-Secret: <that secret>`. Leave it unset to keep
  donations off (the endpoint returns 404).
- **Volunteers.** Anyone can `HAL9001 contribute https://hal9001.io` to donate CPU; the coordinator
  re-verifies every submission. (Rate-limited; a stronger anti-abuse pass is still a to-do.)
- **Updating.** Rebuild the publish, `scp` it over `/opt/hal9001`, `systemctl restart hal-swarm
  hal-dashboard`.
- **Logs.** `journalctl -u hal-dashboard -f` and `journalctl -u hal-swarm -f`.
