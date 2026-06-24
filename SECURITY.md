# Security Policy

HAL9001 is a self-extending AI agent that runs a live, public service at https://hal9001.io.
Because it compiles and runs code it writes for itself, and because it now takes money (Stripe)
and donated compute from strangers, security is treated as a first-class concern.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Use **GitHub's private vulnerability reporting**:
*Security tab → "Report a vulnerability"* (Private Vulnerability Reporting) on this repository.
That keeps the report confidential until a fix is shipped. Please include reproduction steps and,
where relevant, the affected endpoint or commit.

We aim to acknowledge reports promptly and to credit reporters who want it once a fix is live.

## What is in scope

- The public dashboard and its `/api/*` endpoints (`hal9001.io`).
- The token wallet / Stripe checkout / webhook flow.
- The volunteer-compute path (`/api/target`, `/api/contribute`).
- Anything that could let untrusted input cause code execution, credit a wallet without payment,
  read another visitor's data, or read files off the server.

## Security model (how the system is designed to be safe)

These are invariants the project intends to hold. A report that breaks one of them is a real bug:

- **Secrets never live in the repo.** Real keys (Anthropic, Turso, Stripe) and the SSH deploy key
  live only in `/etc/hal9001/hal.env` on the server (mode `600`) and in GitHub's encrypted Actions
  secret store. Only `*.example` placeholder templates are committed. The CI deploy pipeline ships
  the compiled DLL only and never sees a secret.
- **Visitor input never reaches code generation or compilation.** The interactive surface is a
  fixed, server-side choice menu (the client sends only an opaque choice/pack **id**, mapped
  server-side to a fixed action) and a *tool-less* Q&A path. Even a fully successful prompt
  injection can only make HAL *say* something — it cannot make it execute attacker-supplied code.
- **Wallets can only be credited by a Stripe-signed webhook.** The webhook verifies the Stripe
  signature (constant-time), is idempotent against replay, and fails closed. The browser only ever
  holds an opaque `halvid` cookie — never a balance — and every spend is re-validated server-side.
- **Volunteer compute is trustless.** Donated workers send only numbers; the coordinator
  re-verifies every submission. No code is exchanged, so a malicious volunteer cannot inject code.
- **Static file serving is allow-listed.** Audio is served only from a fixed in-memory map of
  embedded resources; there is no user-controlled file path (no path traversal).

## Operational notes

- The origin server's IP is intentionally published via DNS (`hal9001.io`), so it is **not** a
  secret. Host-level hardening (firewall to 22/80/443, key-only SSH, fail2ban, optional
  CDN/proxy in front of the origin) is the appropriate control, not source-history scrubbing.
