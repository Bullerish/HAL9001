# HAL9001 — Machine Handoff

A complete, self-contained guide to pick up HAL9001 development on a new machine.
Everything in this file assumes you are starting from a fresh checkout with **none**
of the local secrets or tooling yet in place.

> **Repo:** `git@hal9001.github.com:Bullerish/HAL9001.git`
> (GitHub: https://github.com/Bullerish/HAL9001 — **PUBLIC**; see "Pick up here" below)
> **Branch:** `main` (in sync with origin). For the exact commit list and per-feature
> evidence, see `git log` and `README.md` → *Release notes* (newest-first).
> **Where the work is right now:** the project is **LIVE at https://hal9001.io** (bite 22)
> with a token economy + Stripe checkout (bites 23–24) and is mid-way through the
> **"honest & alive dashboard"** track (live node count = done; richer CRT + activity feed = next).
> See §2 for the real current state and §18 for the open user actions.

---

## ⏩ Pick up here — session of 2026-06-24 (live "honest & alive" dashboard)

**The repo is now PUBLIC** (decided this session). It's the core "HAL is real" trust signal — no secrets are committed (CI is secret-free; real secrets live only in `/etc/hal9001/hal.env` on the box). The box IP + root-deploy recipe were scrubbed from the public docs, though the IP remains in earlier git history (see Open threads).

**✅ Picked up + verified on a fresh machine (2026-06-24):** pulled to `99a4a9f`; `dotnet build`
clean (0/0); `racetest` green (naive 8 / Strassen 7, exact verifier accepts correct & rejects
buggy); `timeline` reads the live shared hive and the box is actively thinking (newest event
~19:25Z). The hive now holds a real **4×4 = 56-mul champion (1.14× vs naive)** via recursive
Strassen — so RECORDS ≠ 0 anymore. **Env on this machine:** `ANTHROPIC_API_KEY` is in User scope;
the `TURSO_*` vars were present in-session but **not persisted to User/Machine scope** — set them
in User scope (§5.2) so a fresh terminal can reach the hive. **Deploy was NOT run** (still pending — DO FIRST #1).

**Shipped & LIVE at hal9001.io this session:**
- Live CRT activity feed (`/api/live`) + new tinny grind audio bed
- "Matrices being worked" panel — streams the tensor-search U/V/W grids live (`/api/matrix`)
- "What HAL has grown into" panel (`/api/growth`) + a discoveries explainer (why it reads 0)
- **Function catalog** (`/api/functions`) — every tool HAL has written, each linking to its public source
- "How you know this is real" **proof panel** + a "source on github" header link
- **Typewriter**: the CRT now types the newest function on, char-by-char (length-scaled ~12s, click to skip, respects reduced-motion)
- More curated "suggest a tool" chips (combinatorics / graph theory / calculus / probability)
- Full **SEO**: title/description/keywords/OG/Twitter/JSON-LD, `/robots.txt`, `/sitemap.xml`, `/og.svg`

**▶ DO FIRST on the new machine:**
1. `git push origin main` (if anything is unpushed), then **`gh workflow run deploy.yml`**.
   The catalog-completeness fix (`b2d43cf`) is pushed but **NOT yet deployed** — the last deploy (18:42Z) predates it. Deploying ships it.
2. After deploy, verify: `/api/functions` count jumps (it now also counts visitor-steered tools — e.g. `bayes-theorem-calculator`); `/api/matrix` populates on the **next tensor-search round** (rounds fire ~every 12 min, so give it time).

**Manual box steps (gated — run on the box yourself):**
- **Caddy www→apex redirect:** copy the updated `deploy/Caddyfile` to `/etc/caddy/Caddyfile`, then `systemctl reload caddy` (SEO duplicate-content fix; non-load-bearing).
- **Box hardening** (recommended now the IP is effectively public): SSH key-only (disable password auth), `fail2ban`, consider a non-root deploy user with scoped sudo.

**Open threads / next ideas:**
- **Live "run a function on a sample input"** in the dashboard (deferred) — would let visitors watch HAL's real code execute. Safe path: compile tool source straight from the Turso `showcase` table (needs no git on the box). Strongest realness signal — build next if wanted.
- **Box IP in git history** (commits `7ca38e3`, `4c0dd2f`). Decide: accept it (an exposed IP is effectively permanent → just harden the box) vs rewrite history (BFG/filter-repo + force-push — fully removes it but breaks every commit SHA and the dashboard's new GitHub source links).
- Bite 17 tensor-search rank-7 still unreliable (~1/9) — see the `bite17-search-notes` memory.

**Ops gotchas learned this session (also in memory `dashboard-live-ipc`):**
- `/api/live` + `/api/matrix` are **file-IPC under `/opt/hal9001`** (NOT `/tmp` — both services run `PrivateTmp=true`, so `/tmp` is per-service and not shared). Path = `AppContext.BaseDirectory`.
- **Matmul rounds fire ~every 12 min**, not 2. Live surfaces populate slowly after a redeploy — confirm a round actually fired (newest `matmul-round` ts in `/api/state` vs the deploy time) before calling anything broken.
- The **tool catalog = `capability-commissioned` + `curiosity-resolved` events** — visitor-steered/curiosity tools use the latter; reading only one kind undercounts.
- `curl` to the box TLS-errors in Git Bash (exit 35); use PowerShell `Invoke-WebRequest -UseBasicParsing` to verify endpoints.

---

## 0. TL;DR — quick start on the new machine

```powershell
# 1. Install the .NET SDK (8.0+ ; 9.x is fine — it builds net8.0). See §3.
# 2. Clone (needs the SSH deploy key + ssh config alias from §5.3 first):
git clone git@hal9001.github.com:Bullerish/HAL9001.git
cd HAL9001

# 3. Set the three dev secrets in User scope (see §5 — use your real values):
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','<key>','User')
[Environment]::SetEnvironmentVariable('TURSO_DATABASE_URL','<url>','User')
[Environment]::SetEnvironmentVariable('TURSO_AUTH_TOKEN','<token>','User')
# (open a NEW shell so the vars are visible)

# 4. Build + smoke-test:
dotnet build
dotnet run -- dashboard       # open http://localhost:8765 — the live mission-control UI
dotnet run -- timeline 20     # replay the hive's episodic memory (proves the Turso wiring)
```

If the dashboard renders the hive's real state and `timeline` prints events written by the
live box, this machine is reading the same hive as production. See §19 for the full checklist.

---

## 1. What HAL9001 is

A **self-extending AI agent** written in C# (.NET 8). The governing principle is **the LLM
is a toolsmith, never the oracle**: when asked something it can't do, it asks the LLM to
*write C# code* for the whole class of question, compiles that code in-process with Roslyn,
runs it, and keeps it. The answer is always the output of running compiled code — never text
the model emitted.

Run one instance and it's a self-extending agent. Run several with `swarm` and they form a
peer-to-peer mesh that elects a coordinator (quorum), survives node death (in-flight work
recovery), shares handlers over GitHub, and competitively deliberates (every node writes its
own implementation; the best is scored and adopted).

**What it grew into.** On top of that core, HAL9001 became a persistent, public "hive":

- A **persistent self** living in the shared Turso store — a name, a birth, a self-concept, an
  episodic event log (its autobiography), mood/drives that steer its idle time, and a collective
  "hive mind" voice synthesized from all nodes' thoughts. (The *sentience ladder*, bites 1–13.)
- A **Prime Directive**: race to discover *faster matrix-multiplication algorithms*. The hive
  climbs a size ladder (2×2 → 256×256), scoring candidates by **multiplication count** (an exact,
  hardware-independent fitness instrument — wall-clock is noise at small sizes), converges per size
  on a plateau, and gates real "discoveries" behind a known-best bar + exact verification.
- A **live, watchable web dashboard** (HAL-9000 red-eye aesthetic) that anyone can visit at
  https://hal9001.io, plus a **token economy** (free starter grant + Stripe top-ups) and a
  **trustless volunteer-compute** path so strangers can safely donate CPU.

Full narrative + per-feature release notes are in [`README.md`](README.md). This file is the
*operational* handoff (how to stand it up and run it elsewhere).

---

## 2. Current state

- **`main` == `origin/main`.** All committed code is pushed. (Latest commits are handler adds +
  the honest-dashboard "live node count" work — see `git log`.)
- **The capability/swarm ladder** (rungs 1–5b: Roslyn core, router, typed capabilities,
  composition, shared knowledge with Stable/Live facts) is **done + verified**.
- **The kernel/matmul track** is **done through bite 16** (single-node kernel search;
  Prime Directive race; dual-metric size ladder with plateau convergence; novelty gate with
  exact verification + human-gated discovery artifacts). **Bite 17** (LLM-free tensor-decomposition
  *derivation*) has a **correct, verified framework** but its **discrete search is WIP** — it needs
  a stronger search backend before it derives non-trivial algorithms reliably.
- **Going public is done:** mission-control dashboard (bite 18), trustless volunteer compute
  (bite 19), hardened donations + safe tool-less visitor Q&A (bite 20), daily LLM budget meter +
  token accounting (bite 21), **deployed live at hal9001.io** (bite 22), watchable UI + token
  wallet with free grant + 402 enforcement (bite 23), and **Stripe Checkout** top-ups (bite 24).
- **CI/CD** push-to-deploy via GitHub Actions + an SSH deploy key exists (`.github/workflows/deploy.yml`).
- **In progress — the "honest & alive dashboard" track.** The user's complaint was that the live
  dashboard *looked* dead/fake. Root causes diagnosed: (1) the live box was running a **stale
  binary**; (2) the "active nodes" number was a **fake** count derived from recent event authors,
  counting long-dead ghosts; (3) RECORDS/DISCOVERIES = 0 is **honest** (the search just hasn't
  promoted a champion; the LLM-free backend is the known WIP). **Bite 1 of this track is done +
  verified:** a real `presence` heartbeat table + live node count (see §10). Bites 2–3 (richer CRT,
  activity feed) and the persistence/pricing/join-page work are **not started** (§10, §15, §18).
- **Model default:** `AnthropicClient.cs` is committed with a model constant — see §12.

> ⚠️ **The live site may be running an OLD build.** The honest-dashboard code and possibly other
> recent commits were not necessarily redeployed. **A redeploy is the single highest-leverage
> action** (§8.4, §18). To check for staleness: grep the live HTML for a string you know is only
> in current source; if it's missing, the box is stale.

---

## 3. Prerequisites (toolchain)

- **.NET SDK 8.0 or newer** to build (an SDK builds for its own version *or lower*, so the
  9.x SDK builds the `net8.0` target fine), **and the .NET 8 runtime** to run.
  - The project targets **`net8.0`** on purpose (it's an LTS the installed SDK can build).
    To move to net10 later: install the .NET 10 SDK and change the single
    `<TargetFramework>` line in `HAL9001.csproj`.
- **git** on `PATH` (handler sharing shells out to the `git` CLI).
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp.Scripting`, pinned to **5.3.0**) restores
  automatically on `dotnet build` — nothing to install by hand.
- OS: developed on Windows 11 with **PowerShell**; a Bash shell is also handy for the test
  harness scripts. Nothing is Windows-only except the optional benchmark core-pinning
  (`ProcessorAffinity`), which is platform-guarded. **The live box is Linux** (self-contained publish).

---

## 4. Clone the repo

The remote uses an **SSH alias** (`hal9001.github.com`), so you must set up the deploy key +
ssh config (§5.3) **before** cloning. Then:

```powershell
git clone git@hal9001.github.com:Bullerish/HAL9001.git
cd HAL9001
```

If you'd rather clone over HTTPS first and wire SSH later:
`git clone https://github.com/Bullerish/HAL9001.git` (you'll still need the deploy key to
push, since pushes go over the SSH alias remote).

---

## 5. Secrets & environment — **NOT in git, must be re-created**

Nothing secret is ever stored in the repo. All of the following live in the machine's
environment / `~/.ssh` (dev) or in `/etc/hal9001/hal.env` (the box). **Never commit these, never
paste their values into any file (including this one), and never print their values** — when
checking presence, print only a boolean/length:
`[bool][Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')`.

### 5.1 Anthropic API key (required for any generation / thinking)

```powershell
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','<your-anthropic-key>','User')
```
Used by `AnthropicClient`. Without it, generation/race/synthesis can't run (read-only modes like
`dashboard`/`timeline`/`identity` still work; swarm transport works with keyless stub nodes).

### 5.2 Turso (libSQL) — the shared hive store (identity, events, races, wallets, donations)

```powershell
[Environment]::SetEnvironmentVariable('TURSO_DATABASE_URL','libsql://<db>-<org>.turso.io','User')
[Environment]::SetEnvironmentVariable('TURSO_AUTH_TOKEN','<your-turso-token>','User')
```
Point the new machine at the **same** Turso database the live box uses to see the real hive
(identity, episodic memory, race records, token wallets). `TursoClient` talks the HTTP
`/v2/pipeline` API directly (libsql:// → https://, Bearer token). Without these the agent runs
with the hive off (`[hive knowledge: off]` in the banner).

> After setting User-scope env vars, **open a new shell** — existing shells won't see them.

### 5.3 GitHub SSH deploy key + ssh config alias (needed to pull/push handlers)

The repo `origin` is `git@hal9001.github.com:Bullerish/HAL9001.git`. The host
`hal9001.github.com` is an **ssh config alias**, not a real DNS name — it maps to GitHub with a
specific deploy key. On the new machine, **either**:

**Option A — reuse the existing deploy key** (copy from the old machine):
1. Copy `~/.ssh/hal9001_deploy` (private) and `~/.ssh/hal9001_deploy.pub` to the new machine's
   `~/.ssh/` (keep permissions tight).

**Option B — generate a new key and register it** (cleaner if the old machine is going away):
1. `ssh-keygen -t ed25519 -f ~/.ssh/hal9001_deploy -C "hal9001-sync-<machine>"`
2. Add the **public** key as a **Deploy key with write access** in the GitHub repo
   (Settings → Deploy keys → Add deploy key → tick "Allow write access").

Then, in **both** options, add this block to `~/.ssh/config`:
```
Host hal9001.github.com
    HostName github.com
    User git
    IdentityFile ~/.ssh/hal9001_deploy
    IdentitiesOnly yes
```
Test it: `ssh -T git@hal9001.github.com` (expect GitHub's "successfully authenticated, but …
does not provide shell access" message). Then `git pull` / `git push` work non-interactively.

### 5.4 Production-only secrets (live on the BOX, in `/etc/hal9001/hal.env`)

These never touch a dev machine and never go in git — `deploy/hal.env.example` is the only env
file in the repo (placeholders only). The full annotated list is in
[`deploy/hal.env.example`](deploy/hal.env.example); the security-relevant ones:

| Var | Purpose |
|---|---|
| `ANTHROPIC_API_KEY`, `TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN` | Same as §5.1/§5.2, but for the box. |
| `HAL_PACE=slow` | ~6× slower ambient pace so a 24/7 box stays cheap. |
| `HAL_NODE_ROLE=core` | How a node reports itself in the live count: `core` (the box) vs `volunteer` (donated remote compute). |
| `HAL_DAILY_USD=1.0`, `HAL_PRICE_IN`, `HAL_PRICE_OUT` | Daily LLM budget meter (bite 21); thinking pauses when spent ≥ budget + donations. |
| `HAL_FREE_TOKENS=3` | Free tokens a new visitor gets on first load. |
| `HAL_DONATE_SECRET` | Gates `/api/donate` (404 until set); the boost/fund endpoint's shared secret. |
| `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `HAL_PUBLIC_URL` | Stripe token purchases (bite 24); `/api/checkout` + `/api/stripe-webhook` are **404 until set**. |

---

## 6. Build

```powershell
dotnet build
```
- Generated handlers in `handlers/*.cs` are **excluded from the static build** (compiled at
  runtime). Seed handlers are tracked so a fresh checkout already has a small catalog.
- Tip: when several swarm instances run they lock the DLL. To recompile while they run, build to a
  throwaway dir (`dotnet build -o bin/verify`) and run the DLL directly. (This DLL-lock problem is
  why hired/background workers now publish to their own dir — see `git log` around the "DLL lock" fixes.)
- For the box: `dotnet publish -c Release -r linux-x64 --self-contained -o publish-linux` (§8).

---

## 7. Run modes

The first CLI argument selects a mode (see [`Program.cs`](Program.cs)).

| Command | What it does |
|---|---|
| `dotnet run` (or `dotnet run -- agent`) | Single self-extending agent REPL. Needs `ANTHROPIC_API_KEY`. |
| `dotnet run -- agent host 5000` / `agent join 127.0.0.1 5000` | Two-node agent (phase-1 transport). |
| `dotnet run -- swarm 5001 5002 5003` (each node lists its own port first) | The swarm: mesh + elected coordinator + assign/`deliberate`/`compose`/`remember`. Also runs the Prime Directive race + presence heartbeats. |
| `dotnet run -- kernel [size] [candidates]` | Kernel optimization search (bite 1): generate matmul candidates → compile → verify-correct → benchmark → rank by speedup. Defaults 256, 5. |
| `dotnet run -- dashboard [port]` | **LIVE mission-control web UI** (bite 18). Read-only; polls the hive. Needs `TURSO_*`, no API key. Default port **8765**. Serves the page + `/api/state`, `/api/wallet`, `/api/checkout`, `/api/stripe-webhook`, `/api/donate`, `/api/target`, `/api/contribute`. |
| `dotnet run -- timeline [n]` | Replay the hive's episodic memory from Turso (proves events survive restarts). Needs `TURSO_*`. |
| `dotnet run -- identity` | Print the hive's persisted self (name/born/concept/persona). Needs `TURSO_*`. |
| `dotnet run -- hive` | Speak as the collective consciousness — synthesize all nodes' thoughts into one voice. Needs `TURSO_*` + API key. |
| `dotnet run -- racetest` | Self-test the multiplication-counting harness (no key/hive). Confirms naive 2×2 = 8 muls, Strassen = 7. |
| `dotnet run -- derive [n] [rank]` (or `derive strassen`) | LLM-FREE tensor-decomposition derivation (bite 17). `derive 2 7` should rederive a Strassen-equivalent. No key/hive. (Search is WIP.) |
| `dotnet run -- contribute <url> [secs]` | **Volunteer compute** (bite 19): donate CPU to a coordinator. Sends only numbers; the coordinator re-verifies. |
| `dotnet run -- demo` | Roslyn compile-and-load demonstration. |
| `dotnet run -- host 5000` / `join 127.0.0.1 5000` | Step-2 raw TCP chat. |

---

## 8. The live production deployment (hal9001.io)

**Architecture.** Two **separate** systemd processes on one Linux VPS share the hive **only**
through Turso (they do not talk to each other directly):

- `hal-swarm` — *thinks*: runs the race, journals, answers asks, heartbeats presence. Port **9000**.
- `hal-dashboard` — *serves* the read-only web UI + API on **localhost:8765**.
- **Caddy** fronts the dashboard with automatic Let's Encrypt TLS at `https://hal9001.io`.
- Secrets live only in `/etc/hal9001/hal.env` (chmod 600), read at service start. Never in git, never in CI.

**The box:** a VPS (IP in the private deploy notes), domain `hal9001.io`. The repeatable kit is in
[`deploy/`](deploy/) — read [`deploy/README.md`](deploy/README.md) for the full walk-through.
Summary:

### 8.1 First-time provisioning
1. Linux VPS (Ubuntu 24.04), A records `hal9001.io` + `www` → box IP, `ufw allow 22,80,443`.
2. On your machine: `dotnet publish -c Release -r linux-x64 --self-contained -o publish-linux`,
   then `scp` `publish-linux/*` and `deploy/` to `/opt/hal9001/` on the box.
3. On the box: `cd /opt/hal9001/deploy && bash setup.sh` (creates the `hal` user, installs
   `/etc/hal9001/hal.env` from the template, installs + enables both systemd units).
4. `nano /etc/hal9001/hal.env` → fill real secrets → `systemctl restart hal-swarm hal-dashboard`.
5. Install Caddy, `cp deploy/Caddyfile /etc/caddy/Caddyfile`, `systemctl reload caddy`.

### 8.2 The Caddy gotcha (cost me real time — do not lose this)
HttpListener on the dashboard validates the `Host` header. Caddy **must** rewrite it upstream or
the dashboard returns 404 for everything:
```
reverse_proxy localhost:8765 {
    header_up Host {upstream_hostport}
}
```
If `https://hal9001.io` 404s but `curl localhost:8765` on the box works, this is why.

### 8.3 CI/CD push-to-deploy
[`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) is a **manual** (`workflow_dispatch`)
job: build a linux-x64 self-contained publish, `scp` **only `HAL9001.dll`** to the box, `chown
hal:hal`, restart both services, curl-verify the dashboard. **CI never sees any secret** — it only
needs SSH. Required GitHub repo secrets (Settings → Secrets and variables → Actions):
`DEPLOY_HOST` (=`<box-ip>`), `DEPLOY_USER` (=`<deploy-user>`), `DEPLOY_SSH_KEY` (the **private** half
of a keypair whose public half is in the box's `~/.ssh/authorized_keys`). To auto-deploy on push,
uncomment the `push:` trigger once trusted.

### 8.4 Manual update (when CI isn't wired yet)
Rebuild the publish, `scp` it over `/opt/hal9001`, `systemctl restart hal-swarm hal-dashboard`.
Logs: `journalctl -u hal-dashboard -f` and `journalctl -u hal-swarm -f`.

> **Redeploy is likely the first thing to do on the new machine** (the live box may be stale — §2).

---

## 9. The token economy & Stripe (security model)

The whole point is to **offset LLM cost, not gouge** — a slight margin only. The mechanics:

- **Wallet = server-side, keyed by an opaque `halvid` cookie** (HttpOnly, Secure, SameSite=Lax,
  1-year). Balance lives in the `wallet` table in Turso. New visitors get `HAL_FREE_TOKENS` free.
- **Spending is re-validated server-side** every time (`UPDATE wallet SET tokens = tokens - ?
  WHERE vid = ? AND tokens >= ?`). A visitor cannot console-log their way to more tokens — the
  browser holds only an opaque id, never the balance.
- **The ONLY way to credit a purchased wallet is the Stripe-signed webhook.** `/api/stripe-webhook`
  verifies the signature (constant-time), is **idempotent** (the `stripe_seen` table dedupes replays
  within a window), and **fails closed**. `/api/checkout` (3 packs $3/$10/$25, server-side pricing)
  and the webhook are **404 until `STRIPE_SECRET_KEY` + `STRIPE_WEBHOOK_SECRET` are set**.
- **Donations** (`/api/donate`) are gated by `HAL_DONATE_SECRET` (404 until set) and top up the
  day's LLM allowance ("fund the engine"), separate from per-visitor token packs.

**CARDINAL SECURITY RULE (keep it in force):** *visitor input NEVER reaches the
router/generator/Roslyn.* The visitor Q&A path is a **safe, tool-less** `RespondToVisitorAsync` —
even a successful prompt injection can only make HAL *say* something, never execute code. The
choice menu sends only a choice **ID** mapped server-side to a fixed `(kind, arg)`; checkout sends
only a **pack ID** with server-side pricing. Any future per-user personalization/history must feed
**only** that safe tool-less path — never code-gen.

---

## 10. The "honest & alive dashboard" track (in progress)

The user picked three things via an explicit choice: **(1)** first focus = an *honest & alive*
dashboard; **(2)** identity model = **recovery code (no PII)**; **(3)** swarm participation =
**volunteer compute only**.

**Bite 1 — LIVE node count — DONE + VERIFIED.** Replaces the old fake "active nodes" (which counted
dead event-authors) with real heartbeats:
- `presence(node PK, role, last_seen)` Turso table (in `AgentCore.EnsureHiveAsync`).
- Every swarm node upserts its row every 10s (`SwarmAgent.PresenceLoop` →
  `AgentCore.HeartbeatPresenceAsync`); `HAL_NODE_ROLE` tags it `core` vs `volunteer`.
- The dashboard counts rows fresh within a **45s** window (`CountLivePresenceAsync`) and shows
  `total · N core · N volunteer`. (ISO-8601 "o" timestamps sort lexically, so a string `>=` compare
  *is* a time compare.)
- Verified end-to-end against real Turso: 1 node → `{total:1, core:1}` while the legacy logic still
  showed 4 ghosts; volunteer split → `{total:2, core:1, volunteer:1}`; a dead node ages out in ~45s.
- The legacy event-author `nodes` field is still computed and in the JSON but **no longer used by
  the front-end** (harmless dead field, candidate for cleanup).

**Not started (the rest of this track):**
- **Bite 2 — richer CRT:** stream the actual matrix decompositions/code being produced to the
  green-CRT feed (today it's the FORGE event feed; the user wants to *see the matrices being worked*).
- **Bite 3 — anonymized "other visitors" activity feed:** show that other people are asking
  questions / buying tokens / ramping HAL up (anonymized).
- **Recovery-code persistence + per-user history/personalization** (no PII; feeds ONLY the safe
  tool-less path — see §9).
- **Volunteer-compute join page** surfaced on the site (+ rate-limit `/api/contribute`).
- **Offset-only pricing** (note: Stripe's ~$0.30 + 2.9% per-charge fee is the real floor —
  the $3 pack barely clears it; price the packs so a small margin survives the fee).

---

## 11. The swarm test harness (`_hal_sandbox`) — how to recreate

Swarm/failover testing uses a throwaway **sibling** git repo so generation+push works without
touching the real GitHub repo. It is **not** part of this repo; recreate it if you need multi-node
tests. Key rules (each was a multi-hour trap — honor them):

- Create it at a **sibling** path (e.g. `../_hal_sandbox`), NOT inside this repo, so
  `GitSync.Discover()` (walks up from the DLL's dir) finds the sandbox `.git`, not the real one.
- Layout: `app/` holds the built DLL+deps; `handlers/` holds seed handler `.cs` files; `.git` at root.
- **`app/` MUST be gitignored.** If the DLL is tracked, `git reset --hard <seed>` between runs
  silently reverts your freshly-deployed binary → you test a stale build. Seed commit = handlers
  + `.gitignore` only.
- No `origin` remote: `git pull`/`push` fail fast (non-fatal); local commits still succeed, so
  you can count sandbox commits to verify "exactly one handler generated."
- Reset between runs: `git reset --hard <seed> && git clean -fdq`, then `cp` a fresh build into `app/`.
- **Kill all `dotnet`/`git` before every run** (a lingering node holds a port or `.git/index.lock`).
  Launch nodes with redirected stdin for ordered logs; a node with non-interactive stdin auto-runs
  in "[hired] background worker — no REPL" mode and stays alive.
- Timing for a failover test: nodes need ~12–14s to mesh+load; death detection ~4s after a kill;
  generation is fast on haiku (~5s), slower on opus. Kill *early* (≈2–3s after asking) to catch
  generation in-flight.

---

## 12. Model configuration

The Anthropic model is a single constant in [`AnthropicClient.cs`](AnthropicClient.cs)
(`AnthropicClient.Model`). Two values are used in practice:
- `claude-opus-4-8` — highest quality.
- `claude-haiku-4-5-20251001` — much faster + cheaper; handy for tight test loops (kernel/race runs,
  swarm failover timing) and for keeping the 24/7 box's token burn low.

Switch this one line for quality vs. cost. (On the box, `HAL_PACE=slow` + the `HAL_DAILY_USD` budget
meter are the other two cost levers.)

---

## 13. Project layout

| File | Responsibility |
|---|---|
| `IHandler.cs` | Capability contract: `string Handle(string input)`. |
| `CapType.cs` | Fixed type set + parse/hint/boundary-check; `StabilityKind` (`Stable`/`Live`). |
| `Clock.cs` | Injectable date/time seam for **Live** capabilities. |
| `RuntimeCompiler.cs` | Roslyn: compile source → in-memory assembly (`TryCompileAndLoad` + `TryCompileAssembly`). |
| `HandlerRegistry.cs` / `HandlerLoader.cs` | In-memory catalog; startup compile+register of `handlers/`. |
| `HandlerGenerator.cs` | Ask the LLM for a capability, compile, validate, persist+push. |
| `CapabilityRouter.cs` | Classifier: use existing / commission new / decline. |
| `AnthropicClient.cs` / `TursoClient.cs` | HTTP clients for the LLM and the libSQL hive. |
| `GitSync.cs` | Bounded wrapper over the `git` CLI (never blocks the agent). |
| `AgentCore.cs` | The shared answer path + deliberation + composition + knowledge + **hive wiring** (identity, events, budget, **wallet/Stripe/donations**, **presence heartbeats**). |
| `AgentRepl.cs` | Single-instance + two-node agent REPL. |
| `PeerNode.cs` / `PeerMessage.cs` / `PeerDemo.cs` | Two-node TCP transport + chat demo. |
| `SwarmNode.cs` | N-peer mesh transport (dialing, gossip, churn recovery). |
| `SwarmAgent.cs` | Swarm layer: election/quorum, heartbeats, assignment, in-flight recovery, deliberation, compose, **presence loop**. |
| `HiveIdentity.cs` | The persistent SELF (name/born/concept/persona) + `IdentityStore` + hive-mind synthesis types. |
| `EventLog.cs` | Episodic memory — the hive's autobiographical event log in Turso (`timeline` command). |
| `Mood.cs` / `SelfModel.cs` | Affective drives that steer idle time + self-referential question routing (sentience ladder). |
| `MatrixOps.cs` | Naive reference matmul (oracle + baseline), seeded matrices, tolerance compare, anti-DCE checksum. |
| `Scalar.cs` | Multiplication-**counting** scalar — the hardware-independent fitness instrument for the race. |
| `MatmulRace.cs` | The Prime Directive race (a round: generate → count muls → verify → rank). |
| `MatmulLadder.cs` | The size ladder 2×2 → 256×256 with per-size plateau convergence (bite 15). |
| `MatmulKnownBest.cs` | The known-best bar separating "new to the hive" from "new to the world" (bite 16). |
| `TensorSearch.cs` | LLM-FREE tensor-decomposition derivation framework (bite 17; search WIP). `derive` command. |
| `KernelGenerator.cs` / `KernelBenchmark.cs` / `KernelOptimizer.cs` | Kernel search: generate / time / orchestrate+rank. |
| `ContributeWorker.cs` | Volunteer-compute client (`contribute` command) — numbers in, coordinator re-verifies. |
| `Dashboard.cs` | The live web UI + all `/api/*` endpoints (state, wallet, checkout, stripe-webhook, donate, target, contribute) + the honest live node count. |
| `RoslynDemo.cs` / `Program.cs` | Compile-and-load demo; CLI mode dispatch. |
| `deploy/` | The go-live kit: `setup.sh`, systemd units, `Caddyfile`, `hal.env.example`, `README.md`. |
| `.github/workflows/deploy.yml` | Manual push-to-deploy (DLL-only, secret-free). |
| `handlers/` | Generated, runtime-compiled capabilities (shared via GitHub; excluded from the static build). |

---

## 14. What's done — status

**Capability / swarm ladder (all done + verified):** Roslyn core + LLM generation + GitHub sync;
router (use/commission/decline); swarm mesh, reconnect, coordinator routing, heartbeat detection,
quorum election, in-flight recovery; shared `AgentCore`; competitive deliberation (fan-out, score,
winner-only push, quality floor); typed capabilities; composition (linear + bounded multi-link, cap
2); stored explicit + auto-derived facts with the Stable/Live distinction.

**Kernel / Prime Directive track:**

| Bite | Status |
|---|---|
| Kernel search bite 1 — single node: generate → compile → verify → benchmark → rank | done + verified |
| 14 — Prime Directive: the hive's north star (matmul race) | done |
| 15 — dual-metric size ladder 2→256 with plateau convergence | done |
| 16 — novelty gate: known-best bar + exact verification + human-gated discovery artifacts | done |
| 17 — LLM-free tensor-decomposition derivation | framework done + verified; **discrete search WIP** (needs a stronger backend) |

**Going public:**

| Bite | Status |
|---|---|
| 18 — live mission-control dashboard (`dashboard`, HttpListener + Turso reader) | done |
| 19 — trustless volunteer compute (`contribute`; numbers-only, coordinator re-verifies) + HAL-9000 look | done |
| 20 — hardened donations (secret-gated) + safe tool-less visitor Q&A | done |
| 21 — daily LLM budget meter + token accounting (~$1/day, donations top up) | done |
| 22 — **LIVE at hal9001.io** (VPS, systemd hal-swarm:9000 + hal-dashboard:8765, Caddy TLS) | done |
| 23 — watchable UI + `halvid`-cookie token wallet + free starter grant + 402 enforcement | done |
| 24 — Stripe Checkout token purchases (3 tiers, signed idempotent webhook) | done (404 until env secrets set) |
| CI/CD — GitHub Actions push-to-deploy via SSH deploy key (DLL-only) | done (needs 3 GH secrets — §18) |
| Honest-dashboard bite 1 — LIVE node count via `presence` heartbeats | **done + verified** (see §10) |

---

## 15. Next steps / roadmap

- **Finish the honest-dashboard track (§10):** richer CRT (real matrices/code streaming), anonymized
  activity feed, recovery-code persistence + per-user history, a volunteer-compute join page
  (rate-limited), offset-only pricing.
- **Strengthen the bite-17 derivation backend** so it derives non-trivial fast algorithms (the
  framework + exact verification are correct; the discrete search is the weak link).
- **Distribute the matmul/kernel search across the swarm** (volunteer best-of-N); the open hard
  problem for the *wall-clock* metric is fair benchmarking across heterogeneous machines — note the
  **mult-count** metric sidesteps this, which is why the race uses it.
- **Knowledge track:** fact-in-composition; fact updating/invalidation + provenance-aware overrides.
- **Also open:** ambient state beyond date/time for Live caps; general-N missing links; nested
  composition; collapse the two-node `PeerNode` transport onto `SwarmNode`.

---

## 16. Working-context continuity (Claude Code memory)

Development used **Claude Code**, which keeps machine-local memory at
`~/.claude/projects/<encoded-repo-path>/memory/` (an index `MEMORY.md` + one fact per file:
project plan, build-style, toolchain, repo, test-harness recipe, README policy). These are **not**
in the repo and won't transfer with `git`. Copying the `memory/` directory's contents to the new
machine's corresponding `~/.claude/projects/.../memory/` folder preserves the running project
context. (The encoded folder name differs per machine — it derives from the repo's absolute path.)

---

## 17. Gotchas / lessons learned (the expensive ones)

- **Caddy must `header_up Host {upstream_hostport}`** or the dashboard's HttpListener 404s
  everything behind TLS. (§8.2)
- **The live site can silently run a stale binary.** Grep the live HTML for a current-source-only
  string; if absent, redeploy. (§2, §8.4)
- **PowerShell 5.1 mangles `git commit -m`** with embedded quotes → write the message to a temp file
  and `git commit -F <file>` (then delete it).
- **PowerShell `Invoke-WebRequest` needs `-UseBasicParsing`** in 5.1 (IE engine errors headless).
- **Sandbox `app/` must be gitignored** or `git reset --hard` reverts your deployed build → you test
  stale binaries. (§11)
- **DLL lock under multi-node:** hired/background workers publish to their own dir so a rebuild
  doesn't block on a locked original (see the "DLL lock" commits).
- **Kill all `dotnet`/`git` between swarm runs**, and remove stray `.git/index.lock`.
- **`TursoClient` once choked on REAL/float reads** — fixed in the budget-meter bite; keep numeric
  reads robust.
- **Benchmark timing (wall-clock kernel track):** warm up before measuring (JIT/OSR), take the
  **median** of many individually-timed runs, keep a printed sink so the JIT can't DCE the work.
  (The matmul *race* avoids all this by counting multiplications instead.)
- **NEVER paste/handle/print secrets in chat or commit them.** Secrets go into the box's `hal.env`
  or GitHub's encrypted secret store via the web UI — never through the assistant. Scan the staged
  diff for secrets before every commit.

---

## 18. Open user actions (carried over — do these)

1. **Redeploy the live box** so hal9001.io runs the current build (likely stale — §2). Highest leverage.
2. **Add the 3 GitHub repo secrets** for CI/CD (§8.3): `DEPLOY_HOST=<box-ip>`,
   `DEPLOY_USER=<deploy-user>`, `DEPLOY_SSH_KEY=<private deploy key>`. Then delete any local hal-deploy key files.
3. **Stripe go-live:** put `STRIPE_SECRET_KEY` (`rk_live_…`) + `STRIPE_WEBHOOK_SECRET` (`whsec_…`)
   into the box's `hal.env`; create the webhook endpoint at `https://hal9001.io/api/stripe-webhook`
   subscribed to `checkout.session.completed`. Until set, checkout + webhook are 404.
4. (Optional) Set `HAL_DONATE_SECRET` to enable the donate/fund endpoint.

---

## 19. Smoke-test the new machine

1. `dotnet build` → **Build succeeded**.
2. `dotnet run -- racetest` → confirms naive 2×2 = 8 muls, Strassen = 7 (no key/hive needed).
3. `dotnet run -- timeline 20` (Turso set) → prints real events written by the live box → the
   shared hive is wired.
4. `dotnet run -- dashboard` → open `http://localhost:8765` → the red eye, the race, the live feed,
   and a **live node count** that reflects real heartbeats.
5. `dotnet run` (agent, key set) → ask "capital of Ohio" → reuses the seeded handler, answers
   "Columbus" (no generation).
6. Push test: trivial commit + `git push` → confirms the §5.3 deploy key.
7. (Optional) launch a 3-node swarm and `remember`/ask across nodes → `handled by knowledge` from
   the shared hive.

You're set. Welcome to the new machine.
