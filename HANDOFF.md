# HAL9001 — Machine Handoff

A complete, self-contained guide to pick up HAL9001 development on a new machine.
Everything in this file assumes you are starting from a fresh checkout with **none**
of the local secrets or tooling yet in place.

> **Repo:** `git@hal9001.github.com:Bullerish/HAL9001.git`
> (GitHub: https://github.com/Bullerish/HAL9001 — private)
> **Latest commit at handoff:** `3f4397f` — *Kernel optimization search — bite 1 (single node)*
> **Branch:** `main` (in sync with origin)

---

## 0. TL;DR — quick start on the new machine

```powershell
# 1. Install the .NET SDK (8.0+ ; 9.x is fine — it builds net8.0). See §3.
# 2. Clone (needs the SSH deploy key + ssh config alias from §5.3 first):
git clone git@hal9001.github.com:Bullerish/HAL9001.git
cd HAL9001

# 3. Set the three secrets in User scope (see §5 — use your real values):
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','<key>','User')
[Environment]::SetEnvironmentVariable('TURSO_DATABASE_URL','<url>','User')
[Environment]::SetEnvironmentVariable('TURSO_AUTH_TOKEN','<token>','User')
# (open a NEW shell so the vars are visible)

# 4. Build + smoke-test:
dotnet build
dotnet run -- kernel 128 2     # generate/compile/verify/benchmark/rank on one node
```

If `dotnet run` (default agent mode) or the swarm answers a question and you can push a
generated handler, the machine is fully wired. See §15 for the full smoke-test checklist.

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

There is also a newer, separate direction — **kernel optimization search** — that reuses the
generate-and-compile core but ranks candidate implementations of a fixed compute operation by
**speed** (correctness first, then fastest wins).

Full narrative + per-feature release notes are in [`README.md`](README.md). This file is the
*operational* handoff (how to stand it up elsewhere).

---

## 2. Current state

- **All committed code is pushed**; `main` == `origin/main` at commit `3f4397f`.
- The capability ladder (rungs 1–5b), shared `AgentCore`, typed capabilities, composition
  (single + bounded multi-link), stored knowledge (Turso), auto-derived facts with the
  Stable/Live distinction, and kernel-optimization bite 1 are all **done and verified**. See
  §11 for the status table and `README.md` → *Release notes* for the verified evidence.
- **Model default:** `AnthropicClient.cs` is committed with `claude-haiku-4-5-20251001` (fast +
  cheap, used for the kernel/swarm test loops). Switch to `claude-opus-4-8` for highest quality.
  See §9.

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
  (`ProcessorAffinity`), which is platform-guarded.

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
environment / `~/.ssh` and must be set up fresh on the new machine. **Never commit these or
paste their values into any file (including this one).**

### 5.1 Anthropic API key (required for any generation)

```powershell
[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','<your-anthropic-key>','User')
```
Used by `AnthropicClient`. Without it, generation modes can't run (swarm transport/coordination
can still be exercised — keyless nodes return stubs).

### 5.2 Turso (libSQL) — the shared knowledge/facts hive (optional but needed for facts)

```powershell
[Environment]::SetEnvironmentVariable('TURSO_DATABASE_URL','libsql://<db>-<org>.turso.io','User')
[Environment]::SetEnvironmentVariable('TURSO_AUTH_TOKEN','<your-turso-token>','User')
```
Point the new machine at the **same** Turso database to keep the existing facts
(`capital-of-ohio` = explicit, `is-28-a-perfect-number` = derived, etc.). Without these the
swarm runs fine, just with the facts feature off (`[hive knowledge: off]` in the banner).
`TursoClient` talks the HTTP `/v2/pipeline` API directly (libsql:// → https://, Bearer token).

> After setting User-scope env vars, **open a new shell** — existing shells won't see them.
> To verify presence without printing the value:
> `[bool][Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY','User')`

### 5.3 GitHub SSH deploy key + ssh config alias (needed to pull/push handlers)

The repo `origin` is `git@hal9001.github.com:Bullerish/HAL9001.git`. The host
`hal9001.github.com` is an **ssh config alias**, not a real DNS name — it maps to GitHub with a
specific deploy key. Set up on the new machine, **either**:

**Option A — reuse the existing deploy key** (copy the private key from the old machine):
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

---

## 6. Build

```powershell
dotnet build
```
- Generated handlers in `handlers/*.cs` are **excluded from the static build** (compiled at
  runtime). Three seed handlers are tracked (prime check, temperature converter, US state
  capital) so a fresh checkout already has a small catalog.
- Tip: when several swarm instances run they lock `HAL9001.exe`. To recompile while they run,
  build to a throwaway dir: `dotnet build -o bin/verify`, and run the DLL directly.

---

## 7. Run modes

The first CLI argument selects a mode (see `Program.cs`).

| Command | What it does |
|---|---|
| `dotnet run` (or `dotnet run -- agent`) | Single self-extending agent REPL. |
| `dotnet run -- agent host 5000` / `agent join 127.0.0.1 5000` | Two-node agent (phase-1 transport). |
| `dotnet bin/Debug/net8.0/HAL9001.dll swarm 5001 5002 5003` (one per node, each lists its own port first) | The swarm: mesh + elected coordinator + assign-to-one (`<question>`), `deliberate <question>`, `compose <question>`, `remember <fact>`. |
| `dotnet run -- kernel [size] [candidates]` | **Kernel optimization search** (bite 1): generate N matrix-multiply candidates, compile, verify-correct vs the naive reference, benchmark, rank by speedup. Defaults: 256×256, 5 candidates. E.g. `dotnet run -- kernel 128 4`. |
| `dotnet run -- demo` | Roslyn compile-and-load demonstration. |
| `dotnet run -- host 5000` / `join 127.0.0.1 5000` | Step-2 raw TCP chat. |

---

## 8. The swarm test harness (`_hal_sandbox`) — how to recreate

Swarm/failover testing uses a throwaway **sibling** git repo so generation+push works without
touching the real GitHub repo. It is **not** part of this repo; recreate it on the new machine
if you need to run multi-node tests. Key rules (each was a multi-hour trap — honor them):

- Create it at a **sibling** path (e.g. `../_hal_sandbox`), NOT inside this repo, so
  `GitSync.Discover()` (walks up from the DLL's dir) finds the sandbox `.git`, not the real one.
- Layout: `app/` holds the built DLL+deps; `handlers/` holds seed handler `.cs` files; `.git` at root.
- **`app/` MUST be gitignored.** If the DLL is tracked, `git reset --hard <seed>` between runs
  silently reverts your freshly-deployed binary → you test a stale build. Seed commit = handlers
  + `.gitignore` only.
- No `origin` remote: `git pull`/`push` fail fast (non-fatal); local commits still succeed, so
  you can count sandbox commits to verify "exactly one handler generated."
- Reset between runs: `git reset --hard <seed> && git clean -fdq`, then `cp` a fresh build into `app/`.
- **Kill all `dotnet`/`git` before every run** (a lingering node holds a port or
  `.git/index.lock`). Launch each node via `cmd /c dotnet "<dll>" swarm <ports> > log.txt 2>&1`
  with redirected stdin (ASCII StreamWriter) for ordered logs; kill a node tree with
  `taskkill /T /F /PID <cmdPid>`.
- Timing for a failover test: nodes need ~12–14s to mesh+load; death detection is ~4s after a
  kill; generation is fast on haiku (~5s) and slower on opus. Kill *early* (≈2–3s after asking)
  to catch generation in-flight.

---

## 9. Model configuration

The Anthropic model is a single constant in [`AnthropicClient.cs`](AnthropicClient.cs):
`AnthropicClient.Model`. Two values are used in practice:
- `claude-opus-4-8` — default / highest quality.
- `claude-haiku-4-5-20251001` — much faster + cheaper; handy for tight test loops (e.g. the
  kernel runs and swarm failover timing). The kernel-search runs in the release notes were on
  haiku.

The repo is **committed with `claude-haiku-4-5-20251001`** as the default; change this one line
to `claude-opus-4-8` if you want top quality over speed.

---

## 10. Project layout

| File | Responsibility |
|---|---|
| `IHandler.cs` | Capability contract: `string Handle(string input)`. |
| `CapType.cs` | Fixed type set (`String/Int/Number/Bool/Date`) + parse/hint/boundary-check/value-inference; `StabilityKind` (`Stable`/`Live`). |
| `Clock.cs` | Injectable date/time seam for **Live** capabilities (real clock in prod, injected date under validation). |
| `RuntimeCompiler.cs` | Roslyn: compile source → in-memory assembly. `TryCompileAndLoad` (registers an `IHandler`) + `TryCompileAssembly` (general, unsafe-enabled — used by the kernel search to load a typed delegate). |
| `HandlerRegistry.cs` | In-memory capability catalog (name, description, example, handler, types, stability). |
| `AnthropicClient.cs` | Minimal HTTP client for the Anthropic Messages API (model constant lives here). |
| `TursoClient.cs` | Minimal HTTP client for the Turso/libSQL knowledge store. |
| `CapabilityRouter.cs` | Three-way classifier: use existing / commission new / decline (also infers types + stability). |
| `HandlerGenerator.cs` | Asks the LLM for a general capability, compiles, validates (Stable → trial-run; Live → date-injected validation), persists+pushes. |
| `HandlerLoader.cs` | On startup, compile+register every handler in `handlers/`. |
| `GitSync.cs` | Bounded wrapper over the `git` CLI (pull/commit/push), never blocks the agent. |
| `AgentCore.cs` | The one shared answer path + deliberation + composition + knowledge (store/lookup facts, auto-derive on Stable). |
| `AgentRepl.cs` | Single-instance and two-node (host/join) agent REPL. |
| `PeerNode.cs` / `PeerMessage.cs` / `PeerDemo.cs` | Two-node TCP transport + Step-2 chat demo. |
| `SwarmNode.cs` | N-peer mesh transport (dialing, gossip, churn recovery). |
| `SwarmAgent.cs` | Swarm layer: election/quorum, heartbeats, assignment, in-flight recovery, deliberation, compose. |
| `MatrixOps.cs` | Kernel search: naive reference matmul (oracle + baseline), seeded matrices, tolerance compare, anti-DCE checksum. |
| `KernelGenerator.cs` | Kernel search: LLM writes several varied single-threaded matmul implementations. |
| `KernelBenchmark.cs` | Kernel search: the timing harness (warmup, median-of-N, GC control, sink, quiet scope). |
| `KernelOptimizer.cs` | Kernel search: orchestrate generate→compile→correctness-gate→benchmark→rank→report. |
| `RoslynDemo.cs` | Step-1 compile-and-load demonstration. |
| `Program.cs` | CLI mode dispatch. |
| `handlers/` | Generated, runtime-compiled capabilities (shared via GitHub; excluded from the static build). |

---

## 11. What's done — status

**Capability / swarm ladder (all done + verified):**

| Area | Status |
|---|---|
| Roslyn compile-and-load core; LLM generation; GitHub sync; closed loop | done |
| Capability router (use/commission/decline); general capabilities; runtime safety | done |
| Swarm rungs 1–4b-ii: mesh, reconnect, coordinator routing, heartbeat detection, quorum election, in-flight recovery | done |
| Shared `AgentCore` consolidation | done |
| Competitive deliberation 5a/5b (fan-out, score, winner-only push, quality floor) | done |
| Typed capabilities (`String/Int/Number/Bool/Date`) | done |
| Composition: linear chains; auto-gen 1 missing link; bounded multi-link (cap 2, all-or-nothing) | done |
| Stored knowledge: explicit typed facts in the Turso hive | done |
| Auto-derived facts + Stable/Live distinction (Live never caches; date-injected validation) | done |

**Kernel optimization search:**

| Bite | Status |
|---|---|
| Bite 1 — single node: generate → compile → verify-correct → benchmark → rank (dense matmul) | **done + verified** (`3f4397f`) |
| Bite 2+ — distribute the search across the swarm (volunteer best-of-N) | not started (see §12) |

---

## 12. Next steps / roadmap

- **Knowledge track:** fact-in-composition (a fact's typed value feeding a handler's typed
  input) → fact updating/invalidation + provenance-aware overrides (the stale-fact problem for
  explicit/derived facts; time-staleness is already solved by Live-never-caches).
- **Kernel track (the headline next bite):** distribute the optimization search across the
  swarm — a *volunteer-compute best-of-N*: nodes generate and benchmark candidates, the
  coordinator adopts the fastest **correct** one. The open hard problem is **fair benchmarking
  across heterogeneous machines** (fastest on one node's hardware ≠ fastest on another).
- **Also open:** ambient state beyond date/time for Live caps (net/file, same
  inject-under-validation seam); general-N missing links (lift cap-2); nested composition;
  cloud (swarm beyond loopback); collapse the two-node `PeerNode` transport onto `SwarmNode`.
- An observed cleanup: the naive *reference* matmul has obvious headroom (its column-strided
  `b[p,j]` access is cache-unfriendly), which is why even a "classic i-j-k" candidate measured
  ~3× faster than it. Not a bug — the baseline is deliberately naive — but worth noting.

---

## 13. Working-context continuity (optional)

Development has been done with **Claude Code**, which keeps machine-local memory files at
`~/.claude/projects/<encoded-repo-path>/memory/` (an index `MEMORY.md` + one fact per file:
project plan, build-style, toolchain, repo, test-harness recipe, README policy). These are
**not** in the repo and won't transfer with `git`. They're optional, but copying the `memory/`
directory's contents to the new machine's corresponding `~/.claude/projects/.../memory/` folder
preserves the running project context for future Claude Code sessions. (The encoded folder name
will differ on the new machine because it derives from the repo's absolute path.)

---

## 14. Gotchas / lessons learned (the expensive ones)

- **Sandbox `app/` must be gitignored** or `git reset --hard` reverts your deployed build → you
  silently test stale binaries. (§8)
- **Process.Start handle-inheritance + git:** `git add` could wedge while the agent held open
  sockets. `GitSync` gives git its own stdin and closes it, and bounds every git call to ~20s.
- **PowerShell `Invoke-WebRequest` needs `-UseBasicParsing`** in Windows PowerShell 5.1 (the IE
  engine errors in non-interactive mode) — relevant when probing Turso by hand.
- **Kill all `dotnet`/`git` between swarm runs**, and remove a stray `.git/index.lock`.
- **Don't run `-ExecutionPolicy Bypass`** — blocked here; run script bodies inline.
- **Benchmark timing is the crux of the kernel track:** warm up before measuring (JIT/OSR),
  take the **median** of many individually-timed runs (not a single timing — GC/scheduler
  outliers showed as ~3× max spikes the median correctly ignored), and keep a printed sink so
  the JIT can't dead-code-eliminate the work.

---

## 15. Smoke-test the new machine

1. `dotnet build` → **Build succeeded, 0 warnings, 0 errors**.
2. `dotnet run -- kernel 128 2` → reference baseline prints; candidates compile + pass
   correctness; a ranked table with a winner + speedup prints. (No key? It will fail at the
   generate step — that confirms §5.1 is missing.)
3. `dotnet run` (agent) → ask "capital of Ohio"; it should reuse the seeded `get-us-state-capital`
   handler (no generation) and answer "Columbus".
4. Push test: make a trivial commit and `git push` → confirms §5.3 (deploy key) works.
5. (If Turso is configured) launch a 3-node swarm and `remember the capital of Ohio is Columbus`
   on one node, ask it on another → `handled by knowledge` from the shared hive.

You're set. Welcome to the new machine.
