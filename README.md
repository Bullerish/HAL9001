# HAL9001

**A self-extending AI agent that writes its own capabilities at runtime — and a swarm of such agents that mesh, elect a leader, survive failure, and competitively deliberate to collectively write, judge, and adopt the best implementation of a new skill.**

HAL9001 is a .NET console application. A single instance can answer a question it has never seen by asking an LLM to *write the code* that finds the answer, compiling that code in-process with Roslyn, running it, and remembering it. Multiple instances form a peer-to-peer swarm that shares those capabilities over GitHub, routes work to a leader, recovers from node death, and — in its most advanced mode — has every node independently implement a new capability and then objectively picks and propagates the winner.

---

## Table of contents

- [What it is](#what-it-is)
- [The core idea: the LLM is a toolsmith, never the oracle](#the-core-idea-the-llm-is-a-toolsmith-never-the-oracle)
- [What it's for](#what-its-for)
- [How it works](#how-it-works)
- [Installation](#installation)
- [Configuration](#configuration)
- [Usage](#usage)
- [Use cases](#use-cases)
- [Project layout](#project-layout)
- [Safety & keys](#safety--keys)
- [Roadmap](#roadmap)
- [Release notes](#release-notes)
- [Maintaining this README](#maintaining-this-readme)

---

## What it is

Most "AI agents" ask a model for an answer and relay it. HAL9001 does something different: when it doesn't know how to do something, it commissions a **reusable tool** for the whole *class* of such questions, compiles it, and runs it. The model writes a general method (e.g. "the capital of any US state"); the running program executes that method to get the concrete answer. The tool is then kept, shared, and reused — the agent literally extends itself while running.

Run one instance and it's a self-extending agent. Run several and they become a **swarm**: a churn-survivable mesh with an elected coordinator, quorum-based failover, in-flight work recovery, and competitive deliberation.

It is built **one verified rung at a time**, heavily commented, with each rung proven before the next is stacked on top (see [Release notes](#release-notes)).

---

## The core idea: the LLM is a toolsmith, never the oracle

This single principle shapes the whole project:

- The LLM **never answers a task directly.** It only ever (a) *classifies* an input and (b) *writes C# code*.
- The **answer is always the output of running compiled code**, not text the model emitted.
- Capabilities are **general**, not one-offs: "capital of Missouri" produces a `get-us-state-capital` tool that then answers "capital of North Dakota" with no new generation.
- Generated code may **bake in a dataset** (small/stable) or **call the network** (large/changing) — the toolsmith decides.

Consequences: answers are reproducible (run the code again), capabilities accumulate and compound, and the swarm can *judge* implementations objectively by running them against test cases.

---

## What it's for

- **A working study of self-extending / self-modifying agents** — runtime code generation, compilation, and adoption, with human-readable internals.
- **A distributed-systems sandbox** — a hands-on, debuggable implementation of mesh networking, failure detection, leader election, quorum, split-brain avoidance, and exactly-once-ish work recovery.
- **A competitive multi-agent "deliberation" engine** — N agents each write their own implementation of a capability; the swarm scores them on generated tests and adopts the best.
- **A teaching codebase** — every file is commented to explain *why*, not just *what*.

---

## How it works

### The self-extension loop (one instance)

```
question ─▶ router (LLM classifies: use / commission / decline)
                │
       ┌────────┼─────────────────────────────┐
       │use     │commission                    │decline
       ▼        ▼                              ▼
  run existing  LLM writes C# ─▶ Roslyn compiles in-memory ─▶ trial-run   conversational
   capability        │            (registers the IHandler)      │          reply, builds
       │             └──────────── retry once on failure ───────┘          nothing
       ▼                                   │
     answer ◀───────── run the compiled capability (30s guard) ◀┘
                                           │
                            persist to handlers/ + commit + push to GitHub
```

A generated capability is an `IHandler { string Handle(string input); }`. It's compiled to a real in-memory assembly (`RuntimeCompiler`), registered in a `HandlerRegistry`, trial-run before it's trusted, and — on success — written to `handlers/` and pushed to a shared GitHub repo so other instances can pull and gain the same skill. All of this is consolidated in **`AgentCore`**, the single answer path shared by every mode.

**Typed capabilities.** Each capability also declares an **input type** and an **output type** from a small fixed set (`String, Int, Number, Bool, Date`). The type is inferred when the capability is commissioned (folded into the existing router/deliberation LLM calls — no extra round-trips), passed into the generation prompt so the handler parses its input robustly (an `Int` capability copes with "7th"), recorded in the registry and the handler file header, and used for a lightweight **boundary check** that returns a clean typed error if a capability is invoked with the wrong kind of input. The handler stays string-based under the hood; types are metadata + a generation guide + a parse-check. Handlers without a declared type are grandfathered as `String → String`.

### The swarm (many instances)

Identical instances launched with `swarm` form a full mesh and add coordination on top of `AgentCore`:

- **Mesh + churn recovery** — every node dials the others; drops are detected and reconnected; clean exits are distinguished from crashes.
- **Elected, term-stamped coordinator** — lowest-port-alive wins a bully election, confirmed by a **quorum** (majority of the known-member set) so two nodes can never both lead (no split-brain). A returning old coordinator steps down via terms.
- **Heartbeat failure detection** — the coordinator beats; followers declare it dead after a timeout (slow ≠ dead).
- **In-flight work recovery** — if the coordinator dies mid-request, the asker re-drives the request to the newly-elected coordinator, with dedup so the answer is delivered once and the handler generated once.
- **Assign-to-one routing** — `<question>` is round-robin assigned to one node, answered, and routed home.
- **Competitive deliberation** — `deliberate <question>` fans the question out to *every* node; each writes its **own** implementation (held locally, not pushed), runs it against coordinator-generated test cases, and returns a candidate. The coordinator collects the slate, **scores** it (test pass-rate, tie-broken by source parsimony), enforces a **quality floor** (must pass a majority), and **pushes only the winner** so the best implementation becomes the swarm's canonical shared handler.
- **Knowledge (facts)** — the hive holds two kinds of thing: **behaviors** (handlers — verbs that compute) and **knowledge** (facts — nouns it knows). `remember the capital of Ohio is Columbus` stores an explicit typed fact (`capital-of-ohio` → `Columbus`, type `String`) in a shared **Turso** table, so every node knows it and it persists across restarts. When a question arrives, routing first does a conservative **knowledge-lookup** (does a stored fact directly answer this?); if so it returns the fact's value with no handler run and no generation, otherwise it falls through to the normal handler / generate / compose flow. Explicit storage only this bite — no auto-derived, updated, or inferred facts yet.
- **Composition** — `compose <question>` answers a multi-step question by chaining typed capabilities: an LLM decomposes it against the live catalog (names + declared types) into an ordered chain, the plan is **displayed before running**, each **seam is type-checked** (step N's output type must equal step N+1's input type), and the chain executes step→step feeding output into input. Up to **two** missing links are **auto-generated** — each type-constrained by its seam position (two adjacent missing links share a consistent invented seam) and validated to the same quality floor as competitive generation — with **all-or-nothing adoption**: only if *every* missing link validates are they all pushed and the chain run; if any fails, nothing is adopted and the catalog is left untouched. **Three or more** missing links fail cleanly with no generation, and a simple question is answered as a single capability rather than being decomposed. (Runs locally on the asking node; every node shares the catalog via GitHub.)

---

## Installation

### Prerequisites

- **.NET SDK 8.0+** to build (a newer SDK can target net8.0), and the **.NET 8 runtime** to run. The project targets `net8.0` on purpose (see `HAL9001.csproj`).
- **git** on your `PATH` (handler sharing shells out to git).
- An **Anthropic API key** for any mode that generates capabilities (the swarm coordination/transport can be exercised without one — keyless nodes return stubs).
- *(Optional, for cross-instance sharing)* an **SSH deploy key** configured for your GitHub handler repo so `git push`/`pull` run non-interactively. No token or key is ever stored in code.
- *(Optional, for the hive's shared knowledge / facts)* a **Turso (libSQL) database** — set `TURSO_DATABASE_URL` and `TURSO_AUTH_TOKEN`. Without them the swarm runs exactly as before, just without the stored-facts feature.

### Build

```bash
git clone <your-fork-of-this-repo> HAL9001
cd HAL9001
dotnet build
```

Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`, pinned to `5.3.0`) is restored automatically. Generated handlers in `handlers/` are **excluded from the static build** — they are compiled at runtime, not by `dotnet build`.

> Tip: when several swarm instances are running they lock `HAL9001.exe`; to recompile while they run, build to a throwaway dir (`dotnet build -o bin/verify`) and/or run the DLL directly (`dotnet bin/Debug/net8.0/HAL9001.dll ...`).

---

## Configuration

### API key (required for generation)

Set `ANTHROPIC_API_KEY` in the environment of each instance that should generate capabilities. **Never commit it.**

```powershell
# Windows PowerShell (current session)
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```
```bash
# bash
export ANTHROPIC_API_KEY=sk-ant-...
```

The model is configured in `AnthropicClient` (`AnthropicClient.Model`).

### Handler sharing (optional)

To share generated handlers across instances/machines, point the repo's `origin` remote at a GitHub repo reachable via an SSH deploy key (read-write). On startup each instance pulls `handlers/` and loads them; on a successful generation it commits+pushes the one new handler. With no remote (or no repo), the agent still works fully — handlers just stay local to the session.

### Hive knowledge / facts (optional)

To enable the hive's shared **facts** store, set the Turso credentials in each node's environment. **Never commit them.**

```powershell
$env:TURSO_DATABASE_URL = "libsql://<db>-<org>.turso.io"
$env:TURSO_AUTH_TOKEN   = "<token>"
```
```bash
export TURSO_DATABASE_URL=libsql://<db>-<org>.turso.io
export TURSO_AUTH_TOKEN=<token>
```

Every node points at the *same* Turso database, so a fact stored by one node is known to all and persists across restarts. On startup each node bootstraps the `facts` table (`CREATE TABLE IF NOT EXISTS`). Without these vars the swarm runs unchanged — the `remember` command and fact-lookup are simply off (`[hive knowledge: off]` in the banner). The client talks Turso's HTTP API directly (no native dependency).

---

## Usage

The first CLI argument selects a mode (`Program.cs`).

### Single self-extending agent (default)

```bash
dotnet run                 # or: dotnet run -- agent
```
Type a request. If no handler matches, it writes/compiles/registers/pushes one and answers, then suggests a follow-up drawn from what it can already do. `exit` to quit.

### Two-node agent (phase 1)

```bash
# terminal 1
dotnet run -- agent host 5000
# terminal 2
dotnet run -- agent join 127.0.0.1 5000
```
Each node answers its own input through `AgentCore`, sends a follow-up to its peer, and answers the peer's questions. A one-round **loop guard** stops the two from volleying forever.

### The swarm

Launch three (or more) instances; each lists its own port first, then its peers:

```bash
dotnet bin/Debug/net8.0/HAL9001.dll swarm 5001 5002 5003
dotnet bin/Debug/net8.0/HAL9001.dll swarm 5002 5001 5003
dotnet bin/Debug/net8.0/HAL9001.dll swarm 5003 5001 5002
```

**Swarm REPL commands:**

| Command | What it does |
|---|---|
| `<question>` | **Assign-to-one**: the coordinator routes it to one node, which answers (using or commissioning a capability); the answer is routed back to you. |
| `deliberate <question>` | **Fan-out**: every node writes its own implementation, runs it against generated tests; the coordinator scores the slate, picks the winner, pushes only the winner, and returns the winning answer. |
| `compose <question>` | **Compose**: decompose a composite question into a linear chain of typed capabilities, display the plan, type-check each seam, then execute step→step. Up to **two** missing links are auto-generated (type-constrained, validated to the quality floor) and adopted **all-or-nothing** (all validate or nothing is kept); **three or more** missing fails cleanly; a simple question is answered as a single capability. |
| `remember <fact>` | **Store knowledge**: write an explicit typed fact to the shared hive (Turso), e.g. `remember the capital of Ohio is Columbus`. Parsed into a key + value; the value's type is inferred. A later question that a stored fact answers is resolved by **knowledge-lookup** — returned directly, with no handler run and no generation. |
| `peers` | Show currently connected peers. |
| `coordinator` | Show the believed coordinator and election term. |
| `pause <secs>` | Test affordance: stop sending heartbeats for N seconds (simulate a hung coordinator). |
| `@<port> <msg>` | Send a direct chat line to one peer. |
| `exit` | Leave the swarm cleanly (broadcasts a goodbye). |

### Other modes

```bash
dotnet run -- demo                 # Roslyn compile-and-load demonstration (+ small REPL)
dotnet run -- host 5000            # Step-2 raw TCP chat: listen
dotnet run -- join 127.0.0.1 5000  # Step-2 raw TCP chat: connect
```

---

## Use cases

- **Research / education on self-modifying agents** — watch an agent recognize a class of problem, author a general tool for it, compile it live, and reuse it.
- **Learning distributed systems by reading runnable code** — election, quorum, heartbeats, split-brain avoidance, and failover are each a small, commented, individually-testable rung.
- **Competitive code generation** — get N independent LLM implementations of a function, scored objectively against tests, with the best automatically adopted (a practical "best-of-N with verification" pattern).
- **A growing, shared capability library** — point several instances at one GitHub repo and let the catalog of vetted, reusable handlers accumulate.
- **A base to extend** — add capability templates, persistent memory, self-reflection, or goal-setting on top of a verified coordination floor.

---

## Project layout

| File | Responsibility |
|---|---|
| `IHandler.cs` | The capability contract: `string Handle(string input)`. |
| `CapType.cs` | The fixed capability type set (`String/Int/Number/Bool/Date`) + parse, prompt-hint, and boundary parse-check helpers. |
| `RuntimeCompiler.cs` | Compile a C# source string to an in-memory assembly with Roslyn, load it, register the handler (with its declared types). |
| `HandlerRegistry.cs` | In-memory catalog of capabilities (name, description, example, handler, **input/output type**). |
| `AnthropicClient.cs` | Minimal HTTP client for the Anthropic Messages API. |
| `TursoClient.cs` | Minimal HTTP client for the hive's shared knowledge store (Turso/libSQL `/v2/pipeline`). Connects via env credentials. |
| `CapabilityRouter.cs` | The three-way classifier: use existing / commission new / decline. |
| `HandlerGenerator.cs` | Asks the LLM for the general capability, compiles, trial-runs, persists+pushes (or holds locally). |
| `HandlerLoader.cs` | On startup, compile+register every handler in `handlers/`. |
| `GitSync.cs` | Thin, bounded wrapper over the `git` CLI (pull / commit / push), never blocks the agent. |
| `AgentCore.cs` | **The one shared answer path** + deliberation support (test-case generation, no-push candidate generation, winner push) + **composition** (decompose → type-check seams → execute a chain; auto-generate + validate up to two missing links, all-or-nothing) + **knowledge** (store/lookup typed facts in the Turso hive). |
| `AgentRepl.cs` | Single-instance and two-node (host/join) agent REPL. |
| `PeerNode.cs` / `PeerMessage.cs` / `PeerDemo.cs` | Two-node TCP transport and the Step-2 chat demo. |
| `SwarmNode.cs` | N-peer mesh transport (dialing, gossip, churn recovery). |
| `SwarmAgent.cs` | The swarm layer: coordinator election/quorum, heartbeats, assignment, in-flight recovery, fan-out deliberation, the `compose` command. |
| `RoslynDemo.cs` | The Step-1 compile-and-load demonstration. |
| `Program.cs` | CLI mode dispatch. |
| `handlers/` | Generated, runtime-compiled capabilities (shared via GitHub; excluded from the static build). |

---

## Safety & keys

- The Anthropic API key and the Turso credentials (`TURSO_DATABASE_URL`, `TURSO_AUTH_TOKEN`) are read **only** from the environment and never written to disk or committed. If any is ever exposed, rotate it.
- Generated handlers are arbitrary C# compiled and run in-process. They are trial-run before being trusted and executed under a timeout, and runtime errors are caught — but this is a research tool: **run it where executing model-written code is acceptable**, and review what lands in `handlers/`.
- Generated code is allowed to access the network (by design). Be aware of that when reviewing handlers.

---

## Roadmap

- **Competitive swarm (rungs 1–5b) — done.** Mesh, churn recovery, quorum election, in-flight recovery, competitive generate/score/adopt with GitHub propagation.
- **Typed capabilities (small version) — done.** Capabilities declare an input/output type from a fixed set; types guide generation and catch obvious input mismatches.
- **Composition (linear) — done.** Decompose a composite question into a chain of typed capabilities, display the plan, type-check each seam, execute step→step.
- **Auto-generate a single missing link — done.** When a chain needs exactly one capability that doesn't exist, generate it (type-constrained by the seam), validate it to the quality floor, adopt it, and complete the chain.
- **Bounded multi-link generation (cap 2, all-or-nothing) — done.** A chain may have up to two missing links; each is generated + validated, and both are adopted atomically — or, if either fails, nothing is adopted and the catalog is left unchanged. Three or more still fails clean.
- **Stored knowledge — typed facts in the Turso hive — done.** The hive holds knowledge (facts), not just behaviors: `remember` stores an explicit typed fact in a shared Turso table; routing resolves a matching question by a conservative knowledge-lookup. Cross-node and persistent. Explicit storage only (this release).
- **Next (knowledge track):** auto-deriving facts (cache a handler's output as a fact — pulls in staleness), then fact-in-composition (a fact's typed value feeding a handler's input), then fact updating/invalidation — each its own bite.
- **Also planned:** general-N missing links (lift the cap-2); nested/recursive composition (chains of chains); stateful capabilities; cloud (swarm beyond loopback); collapsing the two-node `PeerNode` transport onto the N-peer `SwarmNode`; further out, self-reflection and guarded goal-setting. Type inference/generics/coercion and branching remain out of scope.

---

## Release notes

> Newest first. Each rung was verified before the next was built. Commit hashes are on `main`.

### Stored knowledge — typed facts in the Turso hive
The hive can now **know**, not just **do**. A **fact** is a noun (a stored piece of knowledge, `capital-of-ohio` → `Columbus`); a **handler** is a verb (it computes). Facts live in a shared **Turso** table — the first use of Turso — so a fact stored by any node is known to all and persists across restarts (the hive-memory property, realized for facts). This bite is explicit storage + retrieval + routing only — no auto-derived facts, no updating/staleness, no inference.
- **Facts schema:** `facts (key TEXT PRIMARY KEY, value TEXT, type TEXT, updated_at TEXT)`, bootstrapped by any node with `CREATE TABLE IF NOT EXISTS`. The Turso client (`TursoClient`) talks the HTTP `/v2/pipeline` API directly, connecting via `TURSO_DATABASE_URL` + `TURSO_AUTH_TOKEN` from the environment (never hardcoded/committed) — same discipline as the Anthropic key.
- **Explicit storage:** `remember <statement>` → an LLM parse yields the **key** (a short kebab-case identifier of what the fact is about) and **value** (the bare knowledge); the **type** is inferred from the value (`CapTypes.InferFromValue`: "Columbus" → String, "42" → Int, "true" → Bool, a date → Date). `INSERT OR REPLACE` upserts. Explicit only — facts are stored because a node stored them, never auto-derived from handler runs.
- **Routing recognizes knowledge-lookup (3 kinds):** when a question reaches the coordinator, it first runs a **conservative** knowledge-lookup — it lists the hive's fact keys and (only if any exist) asks the LLM whether exactly one fact *is* the answer. On a real match it returns the fact's value directly, with **no handler run and no generation** (the answer is marked `handled by knowledge`). On no match it falls through to the existing **handler → generate → compose** flow, so a question that should run a handler is never stolen by a vaguely related fact. Lookup order: stored fact → existing handler → generate/compose.
- **Typed facts:** a fact carries a declared type (so a fact's typed value can later feed a handler's typed input — not built this bite).
- *Verified (3 nodes, real key + Turso):* a fact stored on **node B** (`remember the capital of Ohio is Columbus` → stored typed `String`) was retrieved by **coordinator A** for a question asked on **node C** (`what is the capital of Ohio` → `[knowledge] retrieved fact 'capital-of-ohio' = Columbus — no handler, no generation`, delivered to C as `handled by knowledge`) — written, read, and asked on three different nodes through the one shared hive. A no-fact question (`is 7 a prime number`) flowed normally to the handler. The fact persisted in Turso after every node was killed. Regression: in-flight recovery after a coordinator kill, election by quorum, and generation all still hold with the knowledge-lookup on the path.

### Bounded multi-link generation (cap 2, all-or-nothing)
Composition can now fill **up to two** missing links in one chain — atomically. The cap is hard at two; general-N is a later rung.
- **Updated count gate:** 0 missing → run; 1 → single-link generation; **exactly 2 → multi-link generation**; **3+ → clean failure** (`cannot compose: N capabilities missing — at most 2 can be generated`) generating nothing.
- **Seam-aware type derivation (adjacent vs separated):** each missing link's types come from a single per-boundary type vector — a present neighbor pins a boundary authoritatively, the chain's overall input/output pins the ends, and when two missing links are **adjacent**, the type at their shared seam comes from decomposition's declared boundary types. Because every boundary has one value, two adjacent invented links read the **same** type at their shared seam, so their types are consistent by construction (verified in the plan display, e.g. `double-number [Int→Int] → write-poem [Int→String]`).
- **Per-link validation:** each missing link is generated `persist:false` (held local, **not** pushed), given its own type-consistent test cases, and must pass the shared 5b majority floor (`ClearsQualityFloor`), with one capped retry — exactly as the single-link rung.
- **All-or-nothing adoption (the headline semantic):** links are pushed only in a final step reached **after every** missing link has validated. If any link fails, the composition fails cleanly and every already-validated sibling is **removed from the registry** (it was never pushed) — so a failed multi-link composition leaves the shared catalog **completely unchanged**: no commits, no pushed handlers, no lingering registry entries.
- *Verified (3 nodes, real key):* **(happy, 2 separated)** with the converter seeded in the middle, `double 50, convert F→C, then say if below freezing` generated `double-number` and `is-below-freezing`, validated both 3/3, **adopted both (exactly two commits)**, and completed (`100 → 37.78°C → "no"`). **(all-or-nothing failure)** `double 5, then write a poem about it` generated+validated `double-number` 3/3 but its sibling failed validation → composition failed and `double-number` was left with **zero trace** (no commit, not in the catalog). **(cap)** a 3-missing chain failed clean with no generation. **(regressions)** 1-missing still does single-link, 0-missing runs without generating, a simple question is not decomposed. Coordination (mesh/election/quorum/heartbeats/in-flight recovery) is unchanged this rung and intact.

### Auto-generate a single missing chain link
Composition no longer fails the moment a needed capability is absent — if a chain needs **exactly one** capability that doesn't exist, it's generated, validated, adopted, and the chain completes. Multi-link generation and nested composition remain later rungs.
- **Missing-link-count gate:** after decomposition, steps are resolved against the registry and the missing ones counted. **Zero** → run as before; **exactly one** → generate it (below); **more than one** → clean failure (`cannot compose: N capabilities missing … only single-missing-link generation is supported`) generating **nothing**.
- **Type-constrained by the seam:** the missing link's required types are fixed by its chain position — input = the previous step's output type (or the chain's overall input type if it's first), output = the next step's input type (or the chain's overall output type if it's last). With only one link missing, its neighbors are always present, so the inner edges are pinned exactly. The plan shows it before anything runs: `… → check-if-below-freezing [Number→Bool] (MISSING — will generate)`.
- **Validated to the 5b quality floor:** the generated link is run against freshly generated, type-consistent test cases and must pass a **majority** (the shared `ClearsQualityFloor`, identical to competitive deliberation) before it may be used. It is generated **without pushing** first; only a link that passes the floor is adopted. If it can't pass (capped at one retry), the whole composition fails cleanly (`couldn't generate a working '…' that passes validation`) and the failed link is discarded — never completing the chain with a bad link.
- **Adopted + propagated exactly once:** a validated link is registered and pushed to GitHub **once**, with its declared types in the header — so the next composite (on any node) that needs it finds it in the catalog and does not regenerate it.
- *Verified (3 nodes, real key; converter seeded, freezing-check absent):* `compose convert 100F to celsius and tell me if it's below freezing` displayed the chain with the missing link marked, generated `check-if-below-freezing [Number→Bool]` (types derived from the seam), validated it **3/3**, adopted it (one commit, `intype=Number/outtype=Bool` header), and completed the chain (`37.78°C` → "no — above freezing"); a second similar composite **reused** it with no regeneration; a chain needing **two** missing links failed cleanly with **no generation**; and an earlier link that scored 0/3 failed the composition cleanly without adoption. Regression: assign-to-one, simple-not-decomposed, and competitive deliberation all intact.

### Composition (linear chains of existing typed capabilities)
A new `compose <question>` path answers a multi-step question by chaining capabilities that **already exist** — it never auto-generates a missing link, and it doesn't do nested/recursive chains or branching (those are later rungs).
- **Decomposition (the judgment step):** an LLM is given the question and the live catalog — every capability's name **and declared input/output types** — and returns `single` / `chain` (ordered names, chosen only from the list) / `none`. It's biased strongly to `single`, so simple questions aren't over-decomposed; a 0/1-step result falls through to the normal single-capability answer path.
- **Plan displayed before execution:** the chosen chain is printed with its types — `[composition] plan: temperature-converter [Number→Number] → freezing-check [Number→Bool]` — *before* anything runs, so decomposition and execution are separately observable.
- **Existing-only:** each named step is resolved against the registry; a name with no capability fails cleanly (`cannot compose: no capability 'X' available`) — it is **not** generated.
- **Type-checked seams (the core safety property):** before executing, every seam is verified — step N's output type must equal step N+1's input type (exact match, no coercion). A mismatch is rejected with a clear error (`'X' outputs String but 'Y' expects Number`) instead of running and producing garbage.
- **Execution + clean partial failure:** the chain runs in order, each step's output fed as the next step's input (with the typed boundary check); if any step errors, the whole composition fails as a unit, naming the step — no half-result is returned as an answer.
- *Verified (3 nodes, real key, two pre-seeded typed capabilities):* `compose convert 100F to celsius and tell me if that's below freezing` decomposed to `temperature-converter [Number→Number] → freezing-check [Number→Bool]`, displayed the plan, ran it (`37.78°C` → "above freezing") with the seam type-checked, and returned the correct answer; a simple question (`capital of Ohio`) was **not** decomposed (answered as a single capability); a composite naming a non-existent capability failed cleanly with **no generation** (handler count unchanged); and re-typing the converter to `Number→String` made the same chain **reject at the seam** (`outputs String but freezing-check expects Number`) before executing. Regression: assign-to-one still answers; rungs 1–5b + typing intact.

### Typed capabilities (small version)
Capabilities are no longer blindly string→string. Each one now declares an **input type** and an **output type** from a fixed, minimal set — `String, Int, Number, Bool, Date` (no custom types, generics, or coercion; those are later rungs).
- **Inference (no extra LLM calls):** the router returns `inputType`/`outputType` when it commissions a `new` capability; a deliberation infers the types *and* generates type-consistent test cases in one combined `PrepareDeliberationAsync` call at the coordinator.
- **Types guide generation:** the generation prompt tells the LLM exactly what to parse and produce (e.g. "input is an Int — parse the integer tolerantly, even from '7th'"), fixing the old parsing fragility.
- **Recorded everywhere:** types live on the in-memory `Capability`, are written into the handler file header (`// hal9001:intype=…/outtype=…`), and are restored on pull. The handler stays string-based under the hood; types are metadata + a generation guide + a boundary check.
- **Boundary parse-check:** before running a handler, if the input can't hold the declared input type (e.g. an `Int` capability invoked with no number), a clean typed error is returned instead of garbage.
- **Coexistence:** existing/older handlers have no type header, so they're grandfathered as `String → String` (their boundary check is a no-op) and keep working unchanged.
- **Deliberation carries types:** every competing candidate for a question targets the *same* coordinator-declared types (so they're comparable), the test cases match those types, and the winner is pushed with its types in the header.
- *Verified (3 nodes, real key):* `deliberate is 12 a perfect number` inferred `Int→Bool`, generated each candidate under those types, and pushed the winner with `intype=Int/outtype=Bool` in its header; `is twelve a perfect number` (no digit) was caught as a clean type mismatch; the grandfathered `get-us-state-capital` (String→String) still answered "Columbus". Regression: assign-to-one in-flight recovery after a coordinator kill still delivered, election by quorum and one-handler push intact.

### Rung 5b — Scoring & winner selection · competitive deliberation complete
The coordinator now **judges** the candidate slate from 5a and adopts the best:
- **Primary metric:** test pass-rate. Candidates that failed to compile (`GenerationFailed`) are disqualified, not ranked.
- **Tie-break:** shortest source (parsimony — simpler code, less to go wrong), then lowest port as a final fully-deterministic tiebreaker, so the *same slate always yields the same winner*.
- **Quality floor:** a winner must pass a **majority** of the test cases to propagate. This is deliberately not "must pass all" — the test cases are LLM-generated and can be wrong, and a majority bar tolerates one bad test while still demanding broad correctness. A suspicious test (one every candidate failed) is surfaced in the output.
- **Winner-only propagation:** exactly **one** handler — the winner's source — is committed+pushed; the losing candidates were generated locally and are discarded. If the best candidate is below the floor, the asker still gets the best-available answer but **nothing is adopted**.
- **Outcome to the asker:** the winning answer is delivered, clearly marked with its score and whether it was adopted.
- *Verified (3 nodes, real key):* `deliberate is 28 a perfect number` → 3 candidates each 3/3 (a three-way tie) → shortest-source rule selected the 1829-char implementation over the 2115- and 2036-char ones → **exactly one** handler pushed → winning answer delivered. Regression: assign-to-one commission still answers and pushes one handler; rungs 1–5a intact.

### Rung 5a — Fan-out and collect (`66285e7`)
Added the `deliberate <question>` command (alongside, not replacing, assign-to-one). The coordinator generates a few test cases for the question, broadcasts a candidate-request to every member; each independently writes its own implementation (**`persist:false` — held locally, never pushed, so N nodes don't spam the repo**), runs it against the tests, and returns a candidate. The coordinator collects them with a **60s collection window** robust to slow / timed-out / failed-to-compile / dead nodes (never blocks forever) and displays the full slate of N competitors with their pass-rates. No winner picked yet. *Verified:* 3 distinct implementations collected (two 3/3, one 1/3) with zero repo commits.

### Consolidation — shared `AgentCore` (`164f2ba`)
Folded the two drifted copies of the answer path (two-node agent and swarm agent) into one `AgentCore`: registry, GitHub sync, three-way classifier, generation+compilation, push, and run-with-timeout behind one serialization gate. Behavior-preserving (only minor progress-message wording converged). Coordination stayed in the swarm layer; the two transports were intentionally **not** merged (deferred as higher-risk). *Verified:* two-node host/join, classifier decline/use/commission, and the full swarm failover suite all reproduced identically.

### Rung 4b-ii — In-flight work recovery (`8fe239a`)
When the coordinator dies mid-request, the answer still reaches the asker, generated exactly once. Asker-side tracking re-drives the request to the newly-elected coordinator (triggered by the coordinator change, ~1s after the election); dedup (`pending` guard + completed-answer cache) prevents a second commissioning; a handler finishing during the election gap delivers directly to the asker. Also hardened `GitSync` (bounded calls + closed stdin) to fix an intermittent `git` wedge under many open sockets. *Verified:* answer delivered after a mid-generation kill with exactly one commissioning; a question answered just before the kill is **not** re-generated.

### Rung 4b-i — Leader election + failover with quorum (`b32fec0`)
The coordinator became an **elected, term-stamped** role. On detected death, the lowest-port live node runs a bully election and only takes office once a **majority of the known-member set** has voted for it — so two nodes can never both lead (no split-brain), even under partition. A returning old coordinator steps down via terms; a slow (not dead) coordinator is not deposed. *Verified:* kill → remaining two elect exactly one by quorum and converge; `pause` triggers no election; a restarted old coordinator follows the new one.

### Rung 4a — Heartbeat failure detection (`d4dbab5`)
The coordinator broadcasts a heartbeat every second; followers declare it `SUSPECTED DEAD` after a 4× timeout (tuned so a brief stall isn't a false positive). Detection only — it named the would-be successor but took no action yet. *Verified:* a short pause is not death; a long pause/kill is.

### Rung 3 — Coordinator routing (`f0b95cd`)
`SwarmAgent` turned the mesh into a swarm-agent: the lowest-port node is the coordinator and round-robin assigns an asked question to one member, which answers via its agent path; the result is routed back to the asker (correlated by request id). Keyless nodes return stubs so routing is testable without a key. *Verified live:* a question routed across three nodes reused a GitHub-shared capability.

### Rung 2 — Reconnection & rejoin (`2efb770`)
The mesh became churn-survivable: a maintenance loop reconnects dropped/late peers; clean exits (a broadcast goodbye) are distinguished from crashes; one connection per pair is guaranteed. *Verified:* kill a node, others reconverge; restart it, the full mesh re-forms.

### Rung 1 — Multi-peer connectivity (`02009e5`)
New `SwarmNode` N-peer transport (full mesh via a dial-higher rule, identity = listen endpoint, per-link write serialization), built as a new class so the verified two-node path stayed untouched. *Verified:* 3 instances meshed; broadcast and directed sends worked; a leave updated the others.

### Three-way classifier (`1af3183`)
The router gained a third outcome: **decline**. Greetings/chitchat/vague input get a conversational reply and build nothing; only genuine tasks reach generation. Stops the agent from force-building a tool for "hello".

### App-generated follow-ups (`6b2b39c`)
Follow-up questions are no longer an LLM call — the app replays a *different* existing capability's example, grounding the conversation in what the agent can actually do and keeping the LLM purely a toolsmith.

### Runtime-safety fix (`2a70fdd`)
A generated handler that compiled but threw at runtime no longer crashes the agent: all execution is guarded, and handlers are **trial-run before being persisted/pushed**, so only code that compiles *and* runs is shared.

### Rung 1a — Capability router + general capabilities (`8affcee`)
Introduced "recognize, don't match": the LLM classifies a request and either reuses an existing capability or commissions a **general** one (handling the whole class), and generated code may bake in data or call the network. The shift from one-off handlers to reusable, described capabilities.

### Step 6 — Closing the loop (`cb98d85`, `e3175bd`)
Two instances exchange a question over the socket; the receiver answers it through the same agent path and returns the result; a loop guard stops infinite volleying. The distributed self-extending loop, end to end.

### Steps 4–5 — GitHub sync (`b586670`, `c69fd44`)
Generated handlers are written to `handlers/` and committed+pushed; on startup each instance pulls and compiles them. Capabilities now propagate between instances.

### Step 3 — LLM-powered generation (`2325ce7`)
On a registry miss, the agent asks the LLM to write an `IHandler`, cleans/validates the reply, compiles it, registers it, and answers — with one capped fix-up retry that feeds compiler errors back.

### Step 2 — TCP peer socket (`545d450`)
Two identical instances connect over TCP with length-prefixed framing — the transport foundation.

### Step 1 — Roslyn compile-and-load core (`c2d1bbb`)
The heart: compile a C# source string into a real, loadable assembly in memory at runtime and execute it. Everything else builds on this.

---

## Maintaining this README

This README is part of the deliverable, not an afterthought. **On every future change, scan this file and update every section it affects** — at minimum add a new entry to [Release notes](#release-notes) (newest first), and revise [How it works](#how-it-works), [Usage](#usage), [Project layout](#project-layout), and [Roadmap](#roadmap) wherever the change touches them.
