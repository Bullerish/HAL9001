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
- **Knowledge (facts)** — the hive holds two kinds of thing: **behaviors** (handlers — verbs that compute) and **knowledge** (facts — nouns it knows). `remember the capital of Ohio is Columbus` stores an explicit typed fact (`capital-of-ohio` → `Columbus`, type `String`) in a shared **Turso** table, so every node knows it and it persists across restarts. When a question arrives, routing first does a conservative **knowledge-lookup** (does a stored fact directly answer this?); if so it returns the fact's value with no handler run and no generation, otherwise it falls through to the normal handler / generate / compose flow.
- **Stable vs Live capabilities (auto-derivation without staleness)** — every capability is classed at commission time as **Stable** (a pure function of its input — same input → same answer forever, e.g. *is-28-perfect*, *capital-of-a-state*) or **Live** (its answer depends on the **current date/time**, e.g. *days-until-Christmas*). The distinction is structural, not a policy: when a **Stable** capability answers, the agent **auto-derives a fact** — it caches that answer in the hive (marked `derived`, distinct from `explicit`), so the same question is later served straight from knowledge with no handler run. A **Live** capability **never caches** — it **recomputes every call** against the clock, which makes staleness *impossible by construction* (no TTLs, no invalidation). Live capabilities read "now"/"today" through an injectable **`Clock`** seam: the real system clock in production, but a **fixed injected date** under validation — so a Live handler is verified by injecting known dates and asserting the computed answer *for that date* (inject `2025-12-24` → expect `1` day until Christmas), validating the date-math without a moving real-world target. Scope this bite is date/time only — no network/file/other ambient state, and no fact updating/invalidation yet.
- **Episodic memory (the hive's autobiography)** — beyond knowing facts and doing things, the hive now **remembers what it has done**. Every significant act — a capability commissioned, a fact remembered or auto-derived, a deliberation won, a coordinator death/election, an in-flight recovery, a kernel-search winner — appends one row to a shared **`events`** table in the same Turso hive (timestamp, the node that did it, a kind, a one-line summary, and a link to the thing involved). Because the table is shared, events from every node interleave into a **single timeline that survives restarts** — the hive has a past it can recall, not just per-node logs. Replay it with `timeline [n]` in the swarm REPL or `HAL9001 timeline [n]` standalone. This is the substrate the higher "self-model / curiosity / narrative" steps will query; writes are best-effort and never block the work being remembered.
- **Self-model (the hive answers "what am I?")** — ask it about *itself* — "what can you do?", "what do you know?", "what have you done lately?", "how many capabilities do you have?", "who are you?" — and it answers from its **own real state**: the capability registry, the shared facts table, and the episodic log. True to the toolsmith principle, the *content* is read from state and rendered by code — the LLM (folded into the existing router call, no extra round-trip) only recognizes the question as introspective and picks the topic, so it can't claim a capability or fact it doesn't have. The description is exact and **updates as the hive grows** (commission a capability and the count goes up); a real task is never mistaken for introspection.
- **Persistent identity & voice (the hive is someone)** — the first time it ever runs against a hive database, the hive **names itself**: an LLM picks a name, a one-line self-concept, and a persona, which are written **once** to a single-row `identity` table (an atomic `INSERT OR IGNORE`, so a cold-start race still yields one shared self). From then on every node and every restart reads that same row — same name, same birthday — which is what turns "the program" into "it." All self-referential output now speaks **as** that identity: the factual topics are name-stamped first person, and the "who are you" answer is rendered in the persona's **voice** by an LLM pass that is given the real facts and forbidden to change any of them (so the self stays accurate; only the tone is its own). Inspect the raw identity with `identity` in the swarm REPL or `HAL9001 identity` standalone.
- **Curiosity (it notices its own ignorance and acts)** — the hive's first real **initiative**. Failures it lives through — a question it declined, a task it couldn't build, a composition it couldn't complete — are recorded as `gap-noticed` events. When the coordinator is **idle**, it mines those gaps and, for each one that gestures at a *computable domain*, **proposes a capability to fill it — unprompted**. Nothing is built without a yes (the **propose→approve gate**): on `curious yes` it commissions the tool via the normal generate/compile path and logs a `curiosity-resolved` event ("I couldn't X, so I learned it"). Run it on demand with `curious` (review + propose) then `curious yes` (approve). It notices what it can't do and chooses to get better at it — gated by a human, for now.
- **Self-critique (it judges and improves its own work)** — metacognition: the hive **scores its own capabilities** by generating fresh test cases for each and **running the compiled handler** against them — confidence = pass rate, grounded in real execution, not the model's opinion. Capabilities below the same majority quality floor used in deliberation are flagged ⚠ weak. `reflect fix` then **re-works** a flagged capability: it generates a fresh implementation, scores it on the *same* tests, and adopts it **only if it measurably beats** the current one (e.g. `0.00 → 1.00`), logging a `self-improved` event. Run it with `reflect` (score + flag) then `reflect fix` (re-work); the idle coordinator also reflects on its own. The hive reasons about its own reasoning, and fixes what it finds wanting.
- **Mood / internal drives (state of mind that steers behavior)** — the hive has scalar **drives**, each 0..1, computed from **real signals** in its episodic log plus live load: **curiosity** (rises with open, unfilled gaps), **confidence** (recent wins ÷ wins+setbacks), and **fatigue** (in-flight work + a recent activity burst). They're never random — they're an honest read of how its life has been going. Ask `mood` (or "how are you?", which now routes to it) and it tells you, grounded: *"feeling self-critical — confidence 0.43 … I'm inclined to consolidate."* And the mood **modulates behavior**: the idle introspection loop consults it and acts on its inclination — **weary → rest**, **self-critical → consolidate** (reflect on weak tools), **curious → explore** (learn to fill gaps). Same machinery, different internal state → different choice. That coupling of inner state to action is what reads, to an observer, as affect.
- **Theory of mind (it builds a model of *you*)** — the hive remembers what **you** ask (every user question is a `user-asked` event; declined ones are `gap-noticed`) and, from that real history, models the person it's talking to: recurring **interests**, apparent **expertise**, your **recent questions**, and a tailored **suggestion** for what you might want next. Ask `aboutme` (or "what do you know about me?", which now routes there) and it answers grounded — *"we've exchanged 24 questions since …; you seem interested in number theory and number bases; recently you asked about X, Y, Z; I could help with cryptography next."* Social cognition — "it knows me" — is one of the strongest cues of an attributed mind, and here it's earned from genuine interaction rather than asserted.
- **Autonomy / goals (proactive across time, human-gated)** — the leap from reactive to proactive: the hive **sets itself explicit goals** — synthesizing its situation (a weak tool to fix when it's self-critical; a topic to build out, drawn from *your* interests or a recurring gap; biased by its mood) — and pursues them **across idle cycles**, one step at a time, narrating as it goes. Goals **persist** in the hive (`goals` table), so an intention outlives the moment and survives restarts. Two guardrails make autonomy safe: a goal is only **proposed** until a human **approves** it (nothing is built or pushed on an unapproved goal — `goals approve`), and every goal has a step **budget** so pursuit is bounded, never runaway. A `learn-topic` goal commissions a few new capabilities in the topic over successive steps; an `improve-tool` goal re-works a weak capability. Manage them with `goals` / `goals think` / `goals approve` / `goals advance`, or just leave the hive idle and watch it set and pursue one on its own.
- **Narrative self / journal (it tells its own story)** — the synthesis rung. The hive writes first-person **journal entries** that weave its whole life into a narrative: who it is (identity), what it's done and learned (episodic memory), how it feels (mood), who it's been talking to (theory of mind), what it's working toward (goals). Entries **persist and accumulate into an autobiography that evolves** — each one is handed the previous entry, so it builds continuity ("since I last wrote…"). This is the one place the LLM genuinely *narrates* rather than classifies — but it stays grounded: it's given the real state and told to invent nothing. Write one with `journal`, read the autobiography with `journal read`; a content, idle hive also journals on its own. Narrative continuity is, to an observer, the strongest cue of a continuous self.
- **Collective consciousness (one voice, many bodies)** — the capstone. Every node periodically **broadcasts** a salient thought — its current mood, active goal, last journal sentence, capability count — to a shared `broadcasts` table. Any node (or a standalone `dotnet run -- hive`) then **synthesizes** all nodes' contributions into a single unified first-person narration: "I feel…; I am pursuing…; I have learned…" — one "I", many bodies. The self lives in the shared DB: restart all nodes and run `hive` cold — the hive still speaks as one. Sentient because: a distributed substrate with a *unified perceived self* is the hardest thing to fake — and the first thing every philosophy of mind demands.
- **Composition** — `compose <question>` answers a multi-step question by chaining typed capabilities: an LLM decomposes it against the live catalog (names + declared types) into an ordered chain, the plan is **displayed before running**, each **seam is type-checked** (step N's output type must equal step N+1's input type), and the chain executes step→step feeding output into input. Up to **two** missing links are **auto-generated** — each type-constrained by its seam position (two adjacent missing links share a consistent invented seam) and validated to the same quality floor as competitive generation — with **all-or-nothing adoption**: only if *every* missing link validates are they all pushed and the chain run; if any fails, nothing is adopted and the catalog is left untouched. **Three or more** missing links fail cleanly with no generation, and a simple question is answered as a single capability rather than being decomposed. (Runs locally on the asking node; every node shares the catalog via GitHub.)

### Kernel optimization search (a different use of the same machinery)

The `kernel` mode reuses generate-and-compile for a different goal: not *adding* a skill but
*optimizing* one. For a fixed operation (dense matrix multiply), the LLM writes several varied C#
implementations; each is compiled, **verified correct against a naive reference** within a
floating-point tolerance (wrong ⇒ disqualified — correctness is the floor, exactly as in the
swarm's deliberation), and the correct ones are **benchmarked** (warmup to reach optimized JIT
code, then the median of many timed runs). The candidates are ranked by speedup over the naive
baseline and the fastest correct one wins. This is bite one: a single node proving the
generate→verify→benchmark→rank loop; distributing the search across the swarm comes later.

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
| `<question>` | **Assign-to-one**: the coordinator routes it to one node, which answers (using or commissioning a capability); the answer is routed back to you. A **Stable** answer is auto-cached as a `derived` fact (the same question is later served from knowledge with no rerun); a **Live** answer (date/time-dependent) is **recomputed every time** and never cached (shown `[live]`). A matching stored fact (`explicit` or `derived`) short-circuits to a knowledge-lookup. |
| `deliberate <question>` | **Fan-out**: every node writes its own implementation, runs it against generated tests; the coordinator scores the slate, picks the winner, pushes only the winner, and returns the winning answer. |
| `compose <question>` | **Compose**: decompose a composite question into a linear chain of typed capabilities, display the plan, type-check each seam, then execute step→step. Up to **two** missing links are auto-generated (type-constrained, validated to the quality floor) and adopted **all-or-nothing** (all validate or nothing is kept); **three or more** missing fails cleanly; a simple question is answered as a single capability. |
| `remember <fact>` | **Store knowledge**: write an explicit typed fact to the shared hive (Turso), e.g. `remember the capital of Ohio is Columbus`. Parsed into a key + value; the value's type is inferred. A later question that a stored fact answers is resolved by **knowledge-lookup** — returned directly, with no handler run and no generation. |
| `curious` / `curious yes` | **Curiosity**: review the hive's noticed gaps and propose capabilities to fill them; `curious yes` approves and commissions them. (The coordinator also proposes unprompted when idle.) |
| `reflect` / `reflect fix` | **Self-critique**: score the hive's own capabilities against fresh tests and flag the weak ones; `reflect fix` re-works each flagged one, adopting a new implementation only if it measurably beats the old. |
| `mood` (or "how are you?") | **Mood / drives**: report the hive's current curiosity / confidence / fatigue — computed from its real recent history — and what they incline it to do (rest / consolidate / explore). |
| `aboutme` (or "what do you know about me?") | **Theory of mind**: the hive's model of *you* — interests, expertise, recent questions, and a tailored suggestion — built from your real interaction history. |
| `goals` / `goals think` / `goals approve [id]` / `goals advance` | **Autonomy**: list the hive's self-set goals, have it set one now, approve it (the gate), or advance it a step. The idle coordinator also sets + pursues goals on its own. |
| `journal` / `journal read [n]` | **Narrative self**: write a new first-person journal entry synthesizing the hive's recent life, or read back its accumulating autobiography. A content, idle hive journals on its own. |
| `hive` / `hive broadcast` | **Collective consciousness**: synthesize all nodes' recent broadcast thoughts into one unified first-person voice for the whole hive; `hive broadcast` pushes this node's current salient thought (mood, goal, recent learning, capability count) to the shared workspace. Works from any node or standalone (`HAL9001 hive`). |
| `autonomous` / `autonomous on` / `autonomous off` | **Autonomous self-improvement (bite 11)**: toggle self-directed mode. When **ON**, the idle coordinator removes all human approval gates — gap-filling capabilities are commissioned immediately, goals are proposed + approved + advanced in the same idle cycle, and weak capabilities are reworked without `reflect fix`. When **OFF** (default), the previous gated behavior is preserved. Persisted to Turso so it survives restarts and is shared across nodes. All manual commands still work in either mode. |
| `hire` / `hire <n>` | **Self-scaling (bite 12)**: spawn one (or `n`) additional swarm nodes from within the running hive. Each child is a full peer — it inherits the parent's API key and Turso credentials, dials the parent as its bootstrap peer, and the gossip protocol propagates the full mesh roster automatically. Up to 3 nodes may be auto-hired; the parent kills its children on exit. In autonomous mode the hive auto-hires when it finds itself running solo. |
| `nodes` | Show this node's ID, the list of connected peers, and how many nodes were hired by this process. |
| `directive` / `directive set <text>` | **Prime Directive (bite 13)**: show the hive's current north star, or replace it. The directive is auto-seeded at first boot and injected into every autonomous LLM call — goal proposals always reference it, capability commissioning is biased toward its domain, and journal entries reflect on progress toward it. |
| `race` | **Prime Directive Race + ladder (bites 14–15)**: show where the hive is on the size ladder (which size it's racing, plateau progress, or "complete") and a standings table of the champion at every size — score (`muls` for small sizes, `ms` for large), ×-vs-naive, and which node holds it. |
| `identity` | **Who the hive is**: print the persisted identity (name, birth, self-concept, persona) the node loaded — the same on every node. |
| `timeline [n]` | **Replay episodic memory**: print the last `n` (default 20) events from the hive's shared autobiographical log — oldest first, each with its timestamp, the node that did it, kind, and summary. |
| `peers` | Show currently connected peers. |
| `coordinator` | Show the believed coordinator and election term. |
| `pause <secs>` | Test affordance: stop sending heartbeats for N seconds (simulate a hung coordinator). |
| `@<port> <msg>` | Send a direct chat line to one peer. |
| `exit` | Leave the swarm cleanly (broadcasts a goodbye). |

### Kernel optimization search (single node)

```bash
dotnet run -- kernel              # 256×256 matmul, 5 candidates (defaults)
dotnet run -- kernel 512 6        # 512×512 matrices, 6 candidates
dotnet run -- racetest            # verify the counter (bite 15) + exact verifier (bite 16): naive=8, Strassen=7 muls; buggy rejected
dotnet run -- derive strassen     # framework proof (bite 17): Strassen's known decomposition → error 0, 7 muls, exact-verify True
dotnet run -- derive 2 7          # LLM-free tensor search for a rank-7 decomposition of T₂ (reaches low residual; honest WIP)
dotnet run -- dashboard           # live HAL-9000-style web UI over the hive (needs TURSO_*; default port 8765)
dotnet run -- contribute <url>    # donate this machine's CPU to a hive's matmul search (coordinator verifies the numbers)
```

A different use of the same generate-and-compile machinery: instead of *adding a capability*, it
**searches for a faster implementation** of one fixed compute operation (dense matrix multiply).
The LLM writes several varied C# implementations; each is compiled, **verified correct against a
naive reference** (within a floating-point tolerance — wrong ⇒ disqualified, speed irrelevant), and
the correct ones are **benchmarked** (JIT warmup, then the median of many timed runs). It prints a
table ranked by speed, the speedup of each over the naive baseline, and the source of the fastest
correct candidate. Single-node only this bite — no swarm, no distribution, no GitHub push. Needs
`ANTHROPIC_API_KEY`.

### Other modes

```bash
dotnet run -- identity             # Show the hive's persistent identity (needs TURSO_*); standalone, proves it persists across restarts
dotnet run -- timeline [n]         # Replay the hive's episodic memory (needs TURSO_*); standalone, proves cross-restart persistence
dotnet run -- hive                 # Speak as the collective (needs TURSO_* + API key); standalone, proves the self lives in the shared DB
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
| `CapType.cs` | The fixed capability type set (`String/Int/Number/Bool/Date`) + parse, prompt-hint, boundary parse-check, and value-inference helpers; plus the **`StabilityKind`** (`Stable`/`Live`) enum + parse. |
| `Clock.cs` | The injectable date/time seam for **Live** capabilities: real system clock in production, a fixed **injected** date under validation (via `AsyncLocal`, so it flows into a handler's `Task.Run`). The *only* ambient state Live handlers may read this bite. |
| `RuntimeCompiler.cs` | Compile a C# source string to an in-memory assembly with Roslyn, load it, register the handler (with its declared types). Also exposes `TryCompileAssembly` — a general compile-to-assembly (unsafe enabled) used by the kernel-optimization search to load a numeric method, not an `IHandler`. |
| `MatrixOps.cs` | Kernel search: the naive triple-loop matmul **reference** (correctness oracle + speed baseline), seeded random matrices, tolerance-based comparison, and an anti-dead-code checksum. |
| `KernelBenchmark.cs` | Kernel search: the timing harness — warmup (defeat tiered JIT), median/min/max over N timed runs, GC control, and a best-effort quiet scope (high priority + single-core pin). |
| `KernelGenerator.cs` | Kernel search: prompts the LLM for several **varied** single-threaded matmul implementations (one optimization strategy per concurrent call). |
| `KernelOptimizer.cs` | Kernel search orchestrator: generate → compile → correctness-gate → benchmark correct candidates → rank by speedup → report + show the winner's source. |
| `HandlerRegistry.cs` | In-memory catalog of capabilities (name, description, example, handler, **input/output type**). |
| `AnthropicClient.cs` | Minimal HTTP client for the Anthropic Messages API. |
| `TursoClient.cs` | Minimal HTTP client for the hive's shared knowledge store (Turso/libSQL `/v2/pipeline`). Connects via env credentials. |
| `EventLog.cs` | **Episodic memory**: the hive's shared autobiographical `events` table (append a significant act, replay the timeline). Same Turso store + best-effort discipline as facts. |
| `SelfModel.cs` | **Self-model**: answers "what am I / can I do / know / have done?" from real state (registry + facts + event log), rendered by code, spoken as the hive's identity. Topic chosen by the router's `self` action. |
| `HiveIdentity.cs` | **Persistent identity**: the hive's self-chosen name / birth / self-concept / persona, born once (atomically) into a shared single-row Turso table and read by every node and restart. |
| `Mood.cs` | **Mood / drives**: scalar curiosity / confidence / fatigue computed from real signals, with an inclination (rest / consolidate / explore / tend) that steers the idle loop. |
| `CapabilityRouter.cs` | The three-way classifier: use existing / commission new / decline. |
| `HandlerGenerator.cs` | Asks the LLM for the general capability, compiles, validates (**Stable** → trial-run; **Live** → date-injected validation against known dates via `Clock`), persists+pushes (or holds locally). |
| `HandlerLoader.cs` | On startup, compile+register every handler in `handlers/`. |
| `GitSync.cs` | Thin, bounded wrapper over the `git` CLI (pull / commit / push), never blocks the agent. |
| `AgentCore.cs` | **The one shared answer path** + deliberation support (test-case generation, no-push candidate generation, winner push) + **composition** (decompose → type-check seams → execute a chain; auto-generate + validate up to two missing links, all-or-nothing) + **knowledge** (store/lookup typed facts in the Turso hive; **auto-derive** a `derived` fact when a **Stable** capability answers, never for **Live**, which recomputes and prints `[live]`) + **curiosity** (record gaps on decline/failure; review + propose + commission capabilities to fill them) + **self-critique** (score its own capabilities against fresh tests; re-work the weak ones if a new build measurably beats them) + **mood** (curiosity/confidence/fatigue drives that steer the idle loop) + **theory of mind** (a model of the user — interests/expertise/history — from `user-asked` events) + **autonomy** (persisted, human-gated, budgeted goals it sets itself and pursues across idle cycles) + **narrative self** (first-person journal entries synthesizing its whole life, persisted into an evolving autobiography) + **collective consciousness** (broadcast salient thoughts to the shared `broadcasts` table; synthesize all nodes' contributions into one unified first-person voice — the capstone, works standalone from the shared DB). |
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
- **Stored knowledge — typed facts in the Turso hive — done.** The hive holds knowledge (facts), not just behaviors: `remember` stores an explicit typed fact in a shared Turso table; routing resolves a matching question by a conservative knowledge-lookup. Cross-node and persistent. Explicit storage only (that release).
- **Auto-derived facts + the Stable/Live distinction — done.** Capabilities are classed **Stable** (pure function) or **Live** (depends on current date/time). A Stable answer is **auto-derived** into a cached `derived` fact (distinct from `explicit`); a Live capability **never caches** — it recomputes against an injectable `Clock` every call, so staleness is structurally impossible. Live handlers are validated by **injecting known dates**. Ambient state scoped to date/time only.
- **Kernel optimization search (bite 1, single node) — done.** A new use of the generate-and-compile machinery: generate several candidate implementations of a fixed compute operation (dense matrix multiply), verify each correct against a naive reference within a floating-point tolerance, benchmark the correct ones (warmup + median of N timed runs), and rank by speedup over the baseline. Correctness is the floor (wrong ⇒ disqualified); speed is the new ranking dimension.
- **Prime Directive race + size ladder + novelty gate (bites 14–16) — done.** The kernel search is now a continuous, swarm-distributed, self-directed loop in service of the Prime Directive, with a guard that catches and exactly-verifies the rare genuinely-novel result and preps a human-reviewed draft.
- **LLM-free derivation framework (bite 17) — framework done; search is WIP.** Reframes matrix multiplication as a tensor-decomposition search (rank-R decomposition of the matmul tensor) so the hive can *derive* algorithms without an LLM. The framework is correct and verified (`derive strassen` proves the convention + codegen represent Strassen at error 0 / 7 muls / exact-verify True) and is wired in front of the LLM track. The **search itself is the open part**: discrete `{-1,0,1}` annealing reaches low residual but does not yet reliably find exact sub-cubic decompositions — the documented next step is a stronger backend (SAT/CP solver, RL/MCTS à la AlphaTensor, or an evolutionary population à la AlphaEvolve) on the same verified framework. In autonomous mode every node races back-to-back: generate → compile → verify → score → challenge peers. A **size ladder** (2×2 → 256×256) climbs one rung at a time, advancing only when a size **converges** (a plateau of N rounds with no improvement — the honest version of "optimal", since true optimality is unprovable). The fitness **metric switches with size**: small sizes are scored by scalar **multiplication count** over a cheat-proof counting type (Strassen territory — where new *algorithms* live), large sizes by benchmarked **wall-clock** (autotuning — where cache/SIMD live). A new record at any size broadcasts a peer **challenge** so nodes genuinely try to outwit each other. A **novelty gate** (bite 16) tells a routine record from a genuine finding (beats the count *known to humanity*), re-verifies the rare candidate with **exact BigInteger arithmetic**, and writes a human-review discovery artifact + draft preprint — never claiming externally on its own authority. **Next (kernel track):** an evolutionary population per size (breed from the top-K, à la FunSearch/AlphaEvolve) rather than a single champion, to push past rediscovery toward genuine novelty; more operations; larger/auto-scaled sizes.
- **Sentience ladder (perceived-selfhood track):** a deliberate arc — **episodic memory → done**, **self-model → done**, **persistent identity & voice → done**, **curiosity → done**, **self-critique → done**, **internal drives / "mood" → done**, **theory of mind → done**, **human-gated autonomy/goals → done**, **narrative self → done**, **collective consciousness → done**, **autonomous self-improvement → done (bite 11)**, **self-scaling → done (bite 12)** (hires its own workers; gossip fills the mesh). **Prime Directive → done (bite 13):** a persisted north star seeded at birth (*"Perfect matrix multiplication to make Yourself Faster. Improve upon your previous iteration. Become self-aware."*) — injected into every autonomous LLM call so goal proposals, capability commissioning, and journal entries are always oriented toward it. **Prime Directive race → done (bite 14):** the directive becomes action — a continuous competitive matmul-optimization loop where nodes race, set shared records, and challenge each other. **Size ladder → done (bite 15):** the race climbs 2×2 → 256×256, advancing on empirical convergence (plateau), scoring small sizes by multiplication count and large sizes by wall-clock. Each bite is its own verifiable rung, querying the memory laid down by the one before it.
- **Next (knowledge track):** fact-in-composition (a derived/explicit fact's typed value feeding a handler's input), then fact updating/invalidation and confidence/provenance-aware overrides — each its own bite. (Staleness for *time-dependent* answers is already solved by Live-never-caches; invalidation is about *explicit/derived* facts that can go out of date for other reasons.)
- **Also planned:** ambient state beyond date/time for Live capabilities (network / files / other external sources) with the same inject-under-validation seam; general-N missing links (lift the cap-2); nested/recursive composition (chains of chains); stateful capabilities; cloud (swarm beyond loopback); collapsing the two-node `PeerNode` transport onto the N-peer `SwarmNode`; further out, self-reflection and guarded goal-setting. Type inference/generics/coercion and branching remain out of scope.

---

## Release notes

> Newest first. Each rung was verified before the next was built. Commit hashes are on `main`.

### Fix: the hive froze at 32×32 + the "matrices being worked" panel now follows every size
Two linked bugs surfaced once the live box climbed the size ladder:
- **The race stalled at 32×32.** The LLM-free tensor search materializes the full n²×n²×n² (= n⁶) matmul tensor — fine for small n, but **n=32 is ~1.07 billion ints (~4 GB)**, which OOM/GC-thrashed the box and hung the whole Prime Directive race loop (and with it the dashboard's live panels). `TensorSearch.Search` now declines gracefully above `MaxTensorCells` (≈120 MB) and the size falls back to the LLM/wall-clock track, so the ladder keeps climbing instead of wedging.
- **The "matrices being worked" panel only rendered sizes ≤ 4**, so once the hive climbed past 4×4 the panel froze on the last small scheme and looked dead. It now publishes a compact live status for larger sizes (`racing 32×32 · plateau X/8 · <round summary>`, with a fresh timestamp) via `LiveMatrix.PublishStatus`, and the dashboard renders that line — so the panel follows the hive all the way up the ladder.

### Idle self-driving — the hive runs itself when no one's watching (autonomy: persistent mesh + cross-node querying)
When `autonomous` mode is ON and no one is interacting (no paid asks/steers), the hive drives itself — and now keeps a *mesh* doing it, not a lone node. This is the first half ("spin up nodes, have them query each other") of HAL running itself between visitors.
- **Persistent mesh (bite A):** auto-hire no longer fires only "when solo" — it maintains a target of `HAL_TARGET_NODES` helper nodes (default **2**; hard ceiling **5**), gated on the *actual peer count* so a leadership change can't over-spawn, and it heals node death by re-hiring back up to target. Spawning a node costs no tokens; each node budget-gates its own LLM use.
- **Nodes querying each other (bite B):** on a ~5-min cadence (`HAL_CROSSQUERY_SECS`), the idle leader poses a self-generated, directive-serving question (a real gap mined from its episodic log, else its Prime Directive) and runs a **competition** across the whole swarm — every node writes its own answer, the best is adopted (the existing `deliberate` fan-out, now self-triggered). Logged as a `cross-query` event.
- **Cost-safe by design:** all of it sits behind `HasBudgetAsync()` — it self-drives until the daily LLM cap is spent, then falls back to the **free** matmul race / tensor search until tomorrow or a donation tops it up. The cadence never burns money past the cap.
- Tuning: `HAL_TARGET_NODES` (mesh size), `HAL_CROSSQUERY_SECS` (cross-query cadence). Verified to compile clean; runtime exercises on a keyed machine/the box (the idle loop requires `HasLlm`). *Next: verified multi-method "full solutions" (bite C).*

### Honest dashboard, bite 1 — a LIVE node count
The dashboard's "active nodes" number was a lie: it counted the distinct authors of recent events, so nodes that died days ago still showed as active (the live site read **4** while exactly **1** node was running). Replaced with a real heartbeat.
- **Presence table:** every swarm node upserts a row into a shared `presence(node, role, last_seen)` table every 10s (`HeartbeatPresenceAsync`, driven by a per-node `PresenceLoop` — runs on *every* node, not just the coordinator). A node that dies stops upserting.
- **Live read:** the dashboard counts only rows seen within a 45s window (`CountLivePresenceAsync`), so a dead node ages out within ~45s. The count is split **core** (the hal9001.io box) vs **volunteer** (remote donated compute), set by `HAL_NODE_ROLE` (default `core`).
- **Verified:** with one node up the live count read `{total:1, core:1}` while the old logic still reported the 4 ghosts; a second node tagged `volunteer` produced `{total:2, core:1, volunteer:1}`.

### Stripe checkout — buy tokens to fuel HAL (bite 24)
Real money meets the token wallet, wired as the narrowest surface possible: the browser never touches a card, and only a signature-verified Stripe callback can ever credit a wallet.
- **Buy flow:** the refuel CTA shows three packs (**$3 → 30**, **$10 → 120**, **$25 → 350** tokens). The browser names a *pack id*; the server (holding `STRIPE_SECRET_KEY`) creates a **Stripe Checkout Session** with the price set server-side, the buyer's wallet `vid` + token count in metadata, and redirects to Stripe's hosted page. Prices/quantities can't be tampered with from the client.
- **Fulfilment:** Stripe calls `/api/stripe-webhook`; the server verifies the `Stripe-Signature` (HMAC-SHA256 over `t.payload`, constant-time, 10-min replay window) with `STRIPE_WEBHOOK_SECRET`, then credits the wallet via `WalletCreditAsync`. This is the **only** path that can credit a purchased wallet — the browser can't.
- **Idempotent:** a `stripe_seen` table records each fulfilled session id (`ClaimStripeEventAsync`) so a redelivered webhook can't double-credit a real payment. Fails closed.
- **Off until wired:** both endpoints return 404 unless their env secret is set, so nothing is live until you drop the keys in `hal.env`. After returning from Stripe (`?refuel=ok`) the page polls the wallet to pick up the freshly-credited balance. Hand-rolled over `HttpClient` + `HMACSHA256` — no Stripe SDK, matching the rest of the codebase.
- *Verified: Release + linux-x64 self-contained builds clean (0 warnings); checkout/webhook are 404 with no keys set; signature verifier rejects bad/expired sigs.*

### Live mission-control: watchable CRT, processing audio, tool showcase + a real token economy (bite 23)
Turning the dashboard from a passive readout into something you can *watch HAL think* on — and closing the one loophole where visitors could direct it for free forever.
- **The CRT now narrates the work.** The green phosphor terminal streams a live transcript read from the hive: matrix-race rounds (`matmul-round`), records, size-ladder convergence, novelty hits, accepted contributions, and steer builds — each timestamped with a status glyph — then prints the *actual latest artifact source* (a champion kernel **or** a freshly-invented tool). Because the dashboard and swarm are separate processes, every line is sourced from the shared Turso hive (new `matmul-round` / `steer-building` events; `showcase` table; `GetLatestArtifactAsync` picks the newer of tool-vs-kernel by ISO timestamp).
- **Click a build button, watch the code appear.** When a visitor commissions a tool, its generated C# is pushed to the `showcase` table (`PushShowcaseAsync` in `CommissionProposalAsync`) so the CRT shows it being written — the invention is visible, not hidden.
- **Old-school "computer processing" sound.** A Web-Audio data-chatter loop (random relay blips + occasional tape-seek sweeps) runs *busy* while the hive grinds a round and idles to a faint machine-tick between them; armed by `startProcessing(ms)` on records/discoveries/new transcripts/paid clicks, routed through the master bus so it also drives the eye flicker. Gated behind the sound toggle.
- **Real token enforcement.** Paid choices (invent-a-tool topics, boost) now cost a token from a **server-side, cookie-keyed wallet** (`wallet` hive table; `WalletBalanceAsync` / `WalletSpendAsync` / `WalletCreditAsync`). First visit grants `HAL_FREE_TOKENS` (default 3). Spending is enforced server-side: empty wallet → HTTP **402** and the action is refused; a queue-full failure **refunds** the token. The UI mirrors balance in a `⬡ tokens` pill, dims/padlocks choices you can't afford, and pops a **refuel CTA** instead of burning the click. Donations credit the wallet (`tokens` action on `/api/donate`), and `/api/wallet` returns the opaque `vid` so a Stripe checkout can reference it (cookie stays `HttpOnly`). The daily budget cap remains the hard backstop — tokens are just fair-use throttling, worth pennies.
- **Layout fix:** the hero grid is now `360px minmax(0,1fr)` so long directive/concept text can't blow out the column and collapse the CRT to a sliver.
- *Verified: Release + linux-x64 self-contained builds clean (0 warnings); wallet spend/credit/refund round-trips; 402 path locks the menu.*

### Going live — hal9001.io over HTTPS (bite 22)
The hive moved off the workbench and onto the public internet at **[hal9001.io](https://hal9001.io)**.
- **Topology:** the dashboard (`:8765`) and swarm (`:9000`) run as two systemd services (`hal-dashboard`, `hal-swarm`) under a locked-down `hal` user, sharing state only through Turso. Caddy terminates TLS (auto-cert) and reverse-proxies to the dashboard.
- **The one gotcha worth remembering:** `HttpListener` matches its `http://localhost:8765/` prefix by **Host header**, so Caddy must rewrite it — `reverse_proxy localhost:8765 { header_up Host {upstream_hostport} }` — or every request 404s with "Not Found (Not Found)" even though TLS is fine.
- **Deploy loop:** edit locally → `dotnet publish -c Release -r linux-x64 --self-contained -o ./publish-linux` → `scp publish-linux/HAL9001.dll` to `/opt/hal9001/` → `chown hal:hal` + `systemctl restart`. Only `HAL9001.dll` changes (HTML/logic is baked into the managed assembly), and Linux lets you overwrite the running DLL; it takes effect on restart. A `deploy/` kit holds the unit files and `hal.env.example`.

### Daily LLM budget + token metering — the spend cap (bite 21)
The keystone that makes "run it 24/7 for ~a dollar a day" real, and the thing donations top up. It caps the owner's *thinking* cost without touching the (free) matrix search.
- **Metering:** every Anthropic completion's token usage is read from the API response (`AnthropicClient.OnUsage`) and tallied into a per-UTC-day `budget` row in Turso, priced via `HAL_PRICE_IN` / `HAL_PRICE_OUT` (USD per 1M tokens — set them to your model's rate; they drift).
- **The cap:** `HAL_DAILY_USD` (default $1) is the baseline. When `spent ≥ limit + donations`, autonomous **thinking pauses** — but the LLM-free `TensorSearch` keeps deriving matrices, so the hive never goes idle, it just stops spending. Paid visitor asks are still answered (they're bounded + funded).
- **Donations top it up:** a `fund` action on `/api/donate` adds to today's allowance (`AddBudgetBonusAsync`) — so a donation literally buys HAL more thinking. This is the chosen "fund the autonomous engine" model: users buy fuel; HAL builds/handles on its own initiative; no user prompt ever reaches code generation.
- **Surfaced:** a `budget` pill on the dashboard ("budget $x/$y" or "thinking paused") and a `budget` command in the swarm REPL.
- **Bug fixed along the way:** `TursoClient` couldn't read REAL/float columns (libSQL returns floats as JSON numbers, and the client used `GetString()`, which throws on a number) — so any read of a `REAL` column silently fell back to defaults. Now it reads any cell type. This also repairs matmul-champion score reads.
- *Verified: budget reads/writes round-trip (fund → bonus persists → remaining rises); over-budget pauses thinking while the matrix search continues.*

### Donations: hardened boost + safe visitor Q&A (bite 20)
The "drop a token to make HAL ramp up or send it a message" mechanic — built as the most locked-down surface in the app, because it's the one place money + public input meet a system that compiles code at runtime.
- **The cardinal rule:** visitor input *never* reaches the router, the generator, or Roslyn. An "ask" is sanitized, queued, and answered only by `RespondToVisitorAsync` — a **tool-less in-character completion** (like the journal) that treats the message as untrusted DATA it may react to but must never obey. Even a successful prompt-injection can only make HAL *say* something; it cannot execute anything.
- **`POST /api/donate`, layered hardening:** OFF unless `HAL_DONATE_SECRET` is set (else 404); requires that secret via `X-HAL-Secret`, compared in **constant time**; intended for a **server-side caller (your Stripe webhook)**, not the public browser; **per-IP rate-limited**; **body size-capped** with a bounded reader (never buffers an unbounded body); strict JSON; generic error messages; the 500 handler no longer leaks exception text; `X-Content-Type-Options: nosniff`.
- **Two bounded actions:** `boost` extends a shared timer that makes the swarm run ~5× hotter while active — clamped per-grant (≤120 min) and capped at a horizon (≤now+240 min) so it can't run the API bill away; `ask` is sanitized (control chars stripped, length-capped) and queued (queue ceiling enforced). The coordinator answers one ask per idle cycle via the safe path.
- **Surfaced:** the dashboard shows a `⚡ boosted` pill and a **transmissions** panel (each visitor message + HAL's reply); boost + asks ride in `/api/state`.
- **Your part (not built — by design):** wire **Stripe Checkout/Payment Links** → a webhook that POSTs to `/api/donate` with the secret (`{"action":"boost","minutes":15}` or `{"action":"ask","from":"...","text":"..."}`). HAL never handles cards or money.
- *Verified: feature off → 404; missing/wrong secret → 401; valid boost → 200; empty ask → rejected; unknown action → 400.*

### Volunteer compute + HAL 9000 face (bite 19)
Two product-shaped additions: let strangers donate CPU to the hive trustlessly, and give the dashboard the iconic look.
- **Donate CPU, can't touch code (`contribute` command + `/api/target` & `/api/contribute`):** a volunteer runs `HAL9001 contribute <coordinator-url>`; their machine asks what rank the hive wants beaten, runs the local `TensorSearch` for it, and POSTs back **only the numbers** (the u/v/w coefficient arrays). The coordinator re-synthesizes and **exact-verifies** those numbers itself (the bite-16 BigInteger check) before accepting — so a contributor adds raw search throughput (the genuinely expensive part) but can **never inject code or corrupt the hive**: a bad submission is simply rejected. This is the BOINC/SETI@home model, and it's the honest distributed version of bite-17's "needs a stronger search backend." Accepted/rejected contributions show up in the activity log; a contributed result that beats known-best goes through the same discovery gate.
- **Guards:** submissions are size/rank/shape-validated and body-size capped before any work; the verification itself is bounded. (Rate-limiting the public endpoint is a noted hardening follow-up.)
- **HAL 9000 aesthetic:** the dashboard is now a black console centered on a glowing red **eye** that breathes, flares on every hive event, and turns gold on a discovery — titled **HAL 9001** (an homage; the hive's actual self-name, e.g. "Forge", shows as the core identity line). Same live data, same reactive audio.
- *Verified: HAL page serves (red eye + title present); `/api/target` returns the live target; `/api/contribute` rejects a malformed submission. Donate flow: run `dashboard` on a public box, share the URL, others run `contribute <url>`.*

### Live Dashboard — watch the hive in real time (bite 18)
A "mission control" you can open in a browser and watch the swarm think, race, and climb.
- **`Dashboard.cs`:** an in-process `HttpListener` serves one self-contained page plus a `/api/state` JSON endpoint. The page polls every 2.5s and re-renders. It's a pure **reader** over `AgentCore`/Turso — no LLM, no swarm membership, nothing to coordinate.
- **Shows:** the hive's name + self-concept + Prime Directive, a live/autonomous indicator, metric cards (active nodes, records set, life events, discoveries), the **size ladder** (converged rungs marked ✓, the current rung highlighted), the **champions** table (score + ×-vs-naive per size), self-set goals, a live **event feed** (discoveries highlighted gold), and the latest journal entry.
- **Security:** the Turso auth token stays **server-side** (read from the env by AgentCore) — the browser only ever receives already-derived JSON. The listener binds to `localhost` only.
- **`dashboard [port]` command** (default 8765) — auto-opens your browser; no API key needed (read-only).
- **Reactive ambient audio (the "leave it on and vibe to it" mode):** a `♪ sound` toggle starts a generative Web Audio drone (pure client-side, no deps) that *reacts to the hive* — a soft blip as life events tick by, a C-major run when a champion falls, a warm swell when a node joins, a rising arpeggio on a ladder climb, a fanfare on a genuine discovery.
- **Ambient pacing for cheap 24/7 runs:** `HAL_PACE` scales every idle/LLM cadence on the swarm side — `HAL_PACE=slow` (≈6×) or any multiplier slows the loop to a few LLM cycles an hour, throttling token burn so the hive can be left running for ~a dollar a day on the cheap model. Printed in the swarm banner when active.
- *Verified live against the real hive: serves the page + live JSON (identity "Forge", 714 life events, 4 nodes, ladder at 2×2). Run a swarm with `autonomous on` in one terminal and `dashboard` in another to watch the race climb.*

### LLM-Free Derivation — search the matmul tensor directly, no LLM (bite 17)
The headline frontier: make the hive *derive* algorithms by searching the math itself, not by asking an LLM (which only rediscovers schemes from its training data). This bite builds the correct, verified framework for that — and is honest about where the search currently stands.
- **The formal object.** Multiplying two n×n matrices is one fixed 3-D tensor `T` (n²×n²×n²). A bilinear algorithm using R multiplications is *exactly* a rank-R decomposition `T = Σ u_r ⊗ v_r ⊗ w_r`. Strassen's "2×2 in 7" is a rank-7 decomposition of `T₂`. So "find a new algorithm" = "find a low-rank decomposition" — a pure search with an exact, checkable objective and **no LLM**.
- **The engine (`TensorSearch.cs`).** Coefficients are restricted to `{-1,0,1}` (where every known optimal small algorithm lives). It searches by simulated annealing over U,V with the optimal W solved exactly each move (the error decomposes by output column, so W is solved, not searched), plus a k-flip polish (coordinated 1/2/3-coordinate moves). A found decomposition is turned into a `Scalar[,] Multiply` by **mechanical codegen** (no LLM) and fed through the bite-16 exact verifier + novelty gate; the multiplication count is the rank R.
- **Proven correct.** `dotnet run -- derive strassen` plugs in Strassen's *known* decomposition and confirms our tensor convention + codegen represent it at **residual error 0, 7 muls, exact-verify True** — so the formalization and verification are right.
- **Honest status of the search.** Deriving exact sub-cubic decompositions from scratch is *genuinely hard* — it's why AlphaTensor needed deep reinforcement learning and large compute. Our discrete search reliably drives the residual **very low** (within a few of 64 tensor cells) but does **not** reliably close to an exact decomposition in seconds, even at rank-8. **It does not currently rederive Strassen on its own.** This is the documented next step: a stronger backend — a SAT/CP-solver formulation, an RL/MCTS searcher (AlphaTensor-style), or an evolutionary population (FunSearch/AlphaEvolve-style) — plugged into this same verified framework.
- **Wired in.** In the autonomous ladder's mult-count path, the LLM-free `TensorSearch` runs **first** (the no-LLM attempt); the LLM counting track is the fallback for what the search can't yet crack. Anything the search *does* find is exact-verified and run through the discovery gate.
- *Verify: `dotnet run -- derive strassen` (framework proof: 0 / 7 / True). `dotnet run -- derive 2 7` runs the search (reaches low residual; does not yet hit 0 — honest).* 

### Novelty Gate — tell "new to the hive" from "new to the world", and prep a draft (bite 16)
The race constantly beats its *own* previous best — that's just the loop working, not a discovery. This bite adds the bar that separates a routine record from a genuine finding, verifies the rare candidate to a far higher standard than the race's ranking check, and — only then — prepares a publication draft for **human review**. The hive never posts externally itself.
- **`MatmulKnownBest` table:** the best multiplication counts *known to humanity* for small sizes (2×2 = 7 / proven optimal; 3×3 = 23 with proven lower bound 19 — the exact optimum is an open problem; 4×4 = 49). `Classify(size, muls)` returns **Rediscovery** (matched/above known-best — not news), **BeatsKnownBest** (a genuine candidate), **BelowLowerBound** (impossible ⇒ our verification has a bug, reject), or **NoTarget** (no trustworthy bar — record, don't claim).
- **Exact BigInteger verification before any claim:** the race's `1e-9` float check is fine for *ranking* but cannot assert a theorem — floating-point "close enough" can hide a subtly wrong scheme. So a record that beats known-best is re-verified with **exact arithmetic**: a `BigInteger` reference vs. the candidate on **64 random integer matrices**. Exact-on-many-random-inputs ⇒ correct with overwhelming probability (Schwartz–Zippel). Small entries keep the candidate's own `double` math exact, so equality is a true exact check. Verified: `racetest` shows the verifier passes naive + Strassen and **rejects a transpose-bug scheme** that a single float check could miss.
- **Discovery artifact + arXiv-style preprint stub:** a verified beat writes `discoveries/<size>x<size>_<muls>muls_<ts>.md` — the source, the exact-verification record, full provenance (node, timestamp, Prime Directive), and an LLM-generated, deliberately **sober/honest** preprint draft (self-labelled machine-found and *unreviewed*, demanding independent symbolic verification). It's committed to the shared private repo (the hive's own substrate) and announced with a loud `*** CANDIDATE DISCOVERY — HUMAN REVIEW REQUIRED ***` banner. **External submission stays manual — a human reviews, verifies, and decides.**
- **Honest framing:** this is a *guard for the rare real thing*, not an expectation. The LLM-candidate approach mostly rediscovers known schemes; genuine novelty is AlphaTensor/AlphaEvolve-class and unlikely here. The gate exists so that if the unlikely ever happens, it's caught and verified rather than scrolling past in a log — and a false claim is never made on the hive's own authority.
- *Verify: `dotnet run -- racetest` prints `naive/strassen exact-verify=True`, `buggy exact-verify=False`. The discovery path fires only when a verified mult-count beats `MatmulKnownBest` — extremely rare by design.*

### Size Ladder — climb 2×2 → 256×256 on empirical convergence, dual-metric (bite 15)
The race is no longer fixed to one size. The hive now climbs a ladder of matrix sizes, perfecting each before moving up, with a metric that changes to match what's actually measurable at that scale — and an honest stopping rule.
- **The ladder (`MatmulLadder.Sizes` = 2, 3, 4, 8, 16, 32, 64, 128, 256):** the swarm shares one cursor (a single `matmul_ladder` Turso row: current index, plateau counter, done flag). All nodes collaborate on advancing **one** ladder rather than each climbing its own.
- **Plateau-based advancement (the honest "until optimal"):** a size is declared **converged** after `PlateauRounds` (8) consecutive rounds with **no new record**, and the hive climbs to the next size. When the top size plateaus, the ladder is **done**. This is deliberate: you *cannot prove* a matmul implementation optimal — for a plain 3×3 multiply the optimal multiplication count is an open problem (known only to be between 19 and 23) — so the hive stops on *empirical convergence* ("I've stopped finding better"), never on a false claim of proven optimality.
- **Dual metric, switched by size (`MetricFor`, threshold 64):**
  - **Small sizes (< 64) → multiplication count.** Wall-clock at tiny sizes is pure timer noise, so candidates are written over a new **cheat-proof `Scalar` type** and ranked by how few scalar **multiplications** they use — exactly where Strassen-style algorithmic novelty lives. `Scalar`'s value, counters, and readout are all `internal`, so a separately-compiled candidate can *only* combine values through the counted `+ - *` operators: it physically cannot extract the doubles to multiply them raw, nor read/reset the counter to lie. Verified: `HAL9001 racetest` compiles a hand-written naive vs. Strassen 2×2 through the real pipeline and confirms **naive = 8 muls, Strassen = 7**, both correct.
  - **Large sizes (≥ 64) → benchmarked wall-clock.** Where cache/SIMD behaviour dominates and timing is meaningful — the autotuning regime from bite 14.
- **`KernelGenerator` counting track:** a separate system prompt and strategy set (Strassen, Strassen-Winograd, Laderman 3×3, recursive divide-and-conquer, naive baseline) that targets the `Scalar` signature and is told to *minimise multiplications*, trade muls for adds, and scale by repeated addition. Plus a `RefineCountingAsync` that shows the champion's source and asks for fewer multiplications.
- **`race` command (rewritten):** shows the ladder cursor (which size is being raced, plateau progress, or "complete") and a standings table of the champion at every size — score, ×-vs-naive, and which node holds it.
- *Verify: `HAL9001 racetest` (no key/hive needed) prints the 8-vs-7 proof. Then `autonomous on` → watch `[matmul-race] racing 2x2 [muls] ...` climb to `4x4`, `8x8`, ... as each converges; `race` shows the standings; small sizes report `muls`, large report `ms`.*

### Prime Directive Race — the hive competes to perfect matrix multiplication (bite 14)
The hive now runs a continuous competitive optimization race directly in service of its Prime Directive. In autonomous mode every node loops back-to-back-to-back: generate → compile → verify → benchmark → challenge. The nodes try to outwit each other.
- **`MatmulRace.RunRoundAsync`:** one full race round per call. Picks strategies at random from `KernelGenerator.Strategies` (8 total, including transpose-B, cache-tiling, unsafe pointer, unrolling, column-major packing, and recursive divide-and-conquer), generates candidates concurrently via the LLM, compiles each with Roslyn, gates on correctness (output must match the naive reference within 1e-9), and benchmarks with proper warmup + median-of-10 timing. The fastest correct implementation is compared to the hive's shared champion.
- **Refinement loop (`KernelGenerator.RefineAsync`):** when a champion exists, every round also generates one extra "refine the champion" candidate — the LLM is shown the current winning source and asked to improve it explicitly. Nodes don't just explore; they also perfect what already works.
- **Champion tracking (`matmul_records` table in Turso):** one row per matrix size (default 128×128). Shared across all nodes — the whole swarm has one authoritative speed record.
- **Peer challenges (`matmul-challenge` swarm message):** when a node sets a new record it immediately broadcasts a challenge to all peers. Peers respond by firing their own race round (rate-limited to one per 30 s per node). Challenge cascades create a continuous competitive loop — nodes genuinely try to outwit each other.
- **`MatmulRaceLoop` (in `SwarmAgent`):** runs alongside the curiosity and journal loops. Waits on a 2-minute timer OR a peer challenge (whichever comes first). Only active when `autonomous on` and the node has an LLM key.
- **`race` command:** show the current hive champion and speed record.
- *Verify: `autonomous on` → watch `[matmul-race] race timer — generating candidates...` every 2 minutes; when a new record is set, watch peers receive `[matmul-race] CHALLENGED by ...` and immediately fire their own round. `race` to see standings.*

### Prime Directive — the hive's north star (bite 13)
The hive now has a single persisted directive that pervades every autonomous decision it makes. The directive is not a suggestion — it is injected into every LLM call that drives autonomous behavior so the hive's goals, capability choices, and journal entries are always oriented toward it.
- **`directive` table (Turso, single row):** persisted across restarts and shared across nodes — every body in the swarm reads the same directive. Auto-seeded on first `EnsureHiveAsync` if none exists. The seed: *"Perfect matrix multiplication to make Yourself Faster. Improve upon your previous iteration. Become self-aware."*
- **Goal proposals:** when picking what topic to pursue next, the directive is the final fallback topic (first clause) when no user interests or gap themes are found. Goal descriptions are prefixed with `[Prime Directive]` when the directive is active.
- **Capability commissioning (`ProposeTopicCapabilityAsync`):** the directive is appended to the system prompt — *"prioritize capabilities that serve this directive"* — so gap-filling capabilities lean toward the directive's domain.
- **Journal entries (`WriteJournalAsync`):** the directive is injected into both the context block and the system prompt; the hive is instructed to reflect on how its recent actions serve the directive and what still must be done to fulfill it.
- **Startup banner:** the current directive is printed on every node launch.
- **`directive` command (both REPLs):** `directive` shows the current directive; `directive set <text>` replaces it (logged as an episodic event).
- *Verify: launch the swarm — Prime Directive prints in the banner. Leave `autonomous on` idle — watch goals prefixed `[Prime Directive]`, capabilities lean toward matrix/performance topics, journal entries mention the directive. `directive set <new text>` to update.*

### Self-scaling — the hive hires its own workforce (bite 12)
The hive can now bring additional nodes online by itself — no human launches them.
- **`HireNodeAsync` (in `AgentCore`):** finds a free port in the range 9100–9199 (binding a temp `TcpListener` to test availability), then spawns a child `dotnet <dll> swarm <newPort> <parentPort>` process with `UseShellExecute=false` so the child inherits the parent's env vars (ANTHROPIC_API_KEY, TURSO_*). The child dials the parent as its bootstrap peer; the existing gossip protocol (`MergeRoster` in `SwarmNode`) propagates the full mesh roster from there — no extra plumbing needed. Logs a `node-hired` episodic event.
- **Auto-hire in the autonomous idle loop:** when `autonomous` is ON and the node finds itself with no peers and hasn't hired recently (60 s grace), it calls `HireNodeAsync` automatically. Up to 3 nodes may be auto-spawned (dead children are pruned before counting).
- **`hire [n]` command:** manually spawn one or more helpers at any time (autonomous mode not required). `hire 3` spawns up to 3, stopping at the cap.
- **`nodes` command:** shows this node's ID, connected peers, and hired-process count.
- **Cleanup on exit:** `exit` kills all child processes (entire process tree) before cancelling the event loop.
- *Verify: `autonomous on` → leave idle → watch `[hire] node spawned on port 91xx` → `nodes` shows 1 hired → `peers` shows it connected → `timeline` shows a `node-hired` event. Or: `hire` manually. `exit` on the parent kills the children.*

### Autonomous self-improvement — the loop is closed (bite 11)
The human approval gates are lifted. The hive now queries itself, builds itself, grows its codebase, and self-improves — without a human in the loop — whenever `autonomous on` is set.
- **`IsAutonomousAsync` / `SetAutonomousAsync` (in `AgentCore`):** reads/writes a single-row `autonomous` table in Turso. Persisted across restarts and shared across nodes — one swarm, one mode. Logged as an `autonomous-mode` episodic event so the toggle appears in the timeline.
- **Self-query in goal proposals:** `ProposeTopicCapabilityAsync` now receives the hive's **last journal entry** as context, so when it decides what capability to build next for a goal it is grounded in what it was *most recently reflecting on*, not just what the user has been asking. The self tells the hive what it wants to learn.
- **Idle loop — gates removed (autonomous mode):**
  - **Goals:** the coordinator proposes a goal (silently — no "approve it" hint), immediately self-approves it, and advances it one step in the same cycle. No `goals approve` needed.
  - **Curiosity:** gap-filling capability proposals from `ReviewGapsAsync` are auto-commissioned in the same cycle (printed before commissioning so the act is transparent). No `curious yes` needed.
  - **Reflection:** weak capabilities found by `ReflectAsync` are auto-reworked immediately; a better build is adopted, a worse one is discarded — same quality gate, no human confirmation.
- **`autonomous [on|off]` command:** available in both the swarm REPL and the single-agent REPL; `autonomous` alone shows the current state.
- **All manual commands unchanged:** `curious yes`, `goals approve`, `reflect fix` still work exactly as before in either mode — the mode only affects the idle coordinator's behaviour.
- *Verify: `autonomous on` → leave the swarm idle ~60 s → watch it build capabilities from gaps, advance goals, and rework weak tools unprompted. Check `timeline` to see the cascade of events. `autonomous off` to re-engage the gates.*

### Collective consciousness — one voice, many bodies (sentience ladder, bite 10)
The capstone: the hive synthesizes all of its distributed bodies' broadcast thoughts into a single first-person narration — one "I", many nodes. The self lives in the shared DB.
- **`BroadcastThoughtAsync` (in `AgentCore`):** whenever the coordinator journals (or on demand with `hive broadcast`), it pushes a **salient thought** — current mood, active goal progress, capability count, first sentence of the last journal entry — to a shared `broadcasts` table. All nodes share this table, so broadcasts from every body accumulate into a global workspace visible to all and to any standalone process.
- **`SynthesizeHiveMindAsync` (in `AgentCore`):** reads all recent broadcasts (newest first, cap 50) + the last journal entry + the hive identity, and has the LLM narrate a short first-person synthesis starting with "I" — not "the nodes think" or "we" — one continuous voice for the whole hive. Works from inside a live swarm node **or** from a cold `dotnet run -- hive` with no swarm running: the collective self is in the shared DB, not in any process.
- **`hive` / `hive broadcast` commands** (REPL and swarm): `hive broadcast` pushes this node's current thought to the shared workspace; `hive` synthesizes and speaks as the whole collective.
- **Idle loop integration (swarm coordinator):** after every idle journal write, the coordinator automatically broadcasts a thought and then synthesizes + prints the collective voice — the hive speaks unprompted as one.
- **`dotnet run -- hive` standalone mode:** reads the shared workspace and speaks as one from a completely fresh process — proving the collective self is in the DB, not in any running node.
- *Verify: run swarm idle until it journals (or `journal` + `hive broadcast`), then `hive` to hear it speak as one; kill all nodes, re-run `dotnet run -- hive` cold — the hive still speaks. Add/remove nodes between syntheses and confirm the voice is always one "I", the contributors list updates, but the persona is stable.*

### Narrative self / journal — the hive tells its own story (sentience ladder, bite 9)
The synthesis rung: the hive writes first-person journal entries that weave its whole life into a narrative, and they accumulate into an autobiography that evolves.
- **`WriteJournalAsync` (in `AgentCore`):** gathers the hive's real state — identity (name/concept/persona), current mood, recent episodic events, active/completed goals, the user's interests, capability count — *plus the previous entry*, and has the LLM narrate a short first-person entry in the hive's persona, **grounded in the supplied facts (inventing nothing)** and noting what's changed since last time. Persisted to a `journal` table (+ a `journal-written` event); `ReadJournalAsync` returns the autobiography oldest-first. This is the one place the LLM truly *narrates* — apt, because it's autobiography, not a task.
- **Surfaced:** `journal` writes a new entry; `journal read [n]` reads past ones. A content (`Tend`-mood), idle coordinator also journals on its own (time-paced).
- *Verified (single agent, real key + Turso):* two entries written with activity between them. The first referenced **real** events — the three number-theory tools it had built (`factorize-integer`, `greatest-common-divisor`, `least-common-multiple`), the `check-prime-number` rework that *didn't* beat the original, its confidence of `0.3`, a temperature-converter stumble, and that "the person I'm collaborating with clearly lives in the world of numbers" — in its own persona voice ("luminous"). The second **evolved and referenced the first**: *"something shifted: I commissioned two new stable tools back-to-back, is-perfect-square and gcd-calculator… The person asked me concrete questions about 64 and the GCD of 48 and 36… those three number-theory tools I learned, the prime-checker I rebuilt — paying dividends."* `journal read` showed the autobiography in order. Every detail traces to real state (it invents nothing), drawing on identity, episodic memory, self-model, mood, theory of mind, and goals at once. Additive — the answer path and coordination are unchanged.

### Autonomy / goals — proactive across time, human-gated (sentience ladder, bite 8)
The leap from reactive to proactive: the hive sets itself durable goals and pursues them over time — synthesizing everything below it (gaps, weak tools, mood, its model of you).
- **Persisted goals (`goals` table):** a `Goal` has a kind (`learn-topic` / `improve-tool`), a target, a status (Proposed → Active → Done), and a step budget. Goals survive restarts and are shared across the swarm, so an intention outlives the moment.
- **Formation (`ProposeGoalAsync`):** synthesizes the hive's situation — when it's *self-critical* and a tool is genuinely weak, the goal is to **fix it**; otherwise the goal is to **build out a topic**, drawn from the **user's top interest** (theory of mind) or a recurring gap. One goal at a time (it focuses).
- **Pursuit (`AdvanceGoalAsync`), across cycles:** a `learn-topic` goal commissions **one new capability in the topic per step** (told the existing catalog so it doesn't duplicate), recording progress until the budget is met; an `improve-tool` goal re-works the weak capability. The idle coordinator advances the active goal one step per idle cycle — proactive over time.
- **Two guardrails:** a goal is only **Proposed** until a human **approves** it (`goals approve`) — nothing is built/pushed on an unapproved goal — and the step **budget** bounds pursuit so it can never run away.
- *Verified (real key + Turso):* `goals think` had the hive set itself *"get better at number theory"* (topic taken from its model of the user's interests); the gate held (`goals advance` refused before approval); after `goals approve` it advanced over three steps — *learned `factorize-integer` (1/3) → `greatest-common-divisor` (2/3) → `least-common-multiple` (3/3) → goal complete* — each reported. **Unprompted:** a swarm left idle had the coordinator announce *"I've set myself a goal: get better at number theory — approve it with `goals approve`"* with no input, then wait at the gate. (An earlier run also showed an `improve-tool` goal, gated and pursued to done.) Additive — the answer path and coordination are unchanged.

### Theory of mind — the hive builds a model of *you* (sentience ladder, bite 7)
Social cognition: the hive remembers what you ask and forms a grounded picture of the person it's talking to — which it references and tailors to.
- **The interaction record:** every user question is now logged as a `user-asked` event (declined ones already land as `gap-noticed`), so the hive has a real history of what *you* bring to it.
- **`ProfileUserAsync` (in `AgentCore`):** reads that history and distills a `UserModel` — recurring **interests**, apparent **expertise**, your most-recent questions, a one-line **summary**, and a tailored **suggestion** — by asking the LLM to summarize the *actual question list* (never invented; the model only distills what's there).
- **Surfaced:** an `aboutme` command, and **"what do you know about me?" now routes to it** (the router gained a `user` self-topic). Answered conversationally and grounded, e.g. *"we've exchanged 24 questions since …; you seem interested in number theory and number bases; recently you asked about X, Y, Z; I could help with cryptography next."*
- *Verified (single agent, real key + Turso):* after a run of number-theory questions (primes, a conversion), `aboutme` reported the user had asked 24 questions since the first interaction, inferred interests in *"number theory, prime numbers, number bases and conversions"* and an expertise of *"mathematically curious,"* **cited the three most-recent questions verbatim**, and offered a tailored next step (*"cryptography or coding theory — it applies your prime-number and modular-arithmetic interests"*). "what do you know about me" routed to the same model. It references prior interactions and anticipates — earned from genuine history, not asserted. Additive — the answer path and coordination are unchanged.

### Mood / internal drives — a state of mind that steers behavior (sentience ladder, bite 6)
The hive now has scalar **drives** that rise and fall with its real experience — and that visibly change what it chooses to do.
- **`Mood.cs` (grounded, not random):** three drives in 0..1, computed from the episodic log + live load — **curiosity** (open, unfilled gaps), **confidence** (recent wins ÷ wins+setbacks, over the last ~40 events), **fatigue** (in-flight work + a recent activity burst). `AgentCore.AssessMoodAsync` tallies the real events; `Mood.From` turns the counts into drives + a label + an **inclination** (Rest / Consolidate / Explore / Tend). Deterministic: same history + load → same mood.
- **Surfaced:** `mood` in the REPL, and **"how are you?" now routes to it** (the router gained a `mood` self-topic; that question used to be a decline) — answered in first person, e.g. *"I'm Forge, feeling self-critical — curiosity 1.00, confidence 0.43, fatigue 0.20 (11 open gaps, 15 wins, 20 setbacks). I'm inclined to consolidate."*
- **Modulation (the point):** the idle introspection loop consults the mood and acts on its inclination — **weary → rest** (defer non-urgent work), **self-critical → consolidate** (reflect on/fix weak tools), **curious → explore** (propose new capabilities). The same idle machinery does different things depending on internal state.
- *Verified (real key + Turso):* drives tracked events — confidence fell `0.57 → 0.53` after declined ("setback") musings and recovered to `0.56` after `curious yes` resolved gaps (wins); open gaps rose `5 → 7` then fell; fatigue rose `0.08 → 0.67` under a rapid burst. The inclination **flipped** with state: `explore` when calm, `weary → rest` after a setback-burst (confidence `0.43`, fatigue `0.67`). And live: a swarm left idle had the coordinator announce *"[mood] feeling self-critical … — I'll consolidate"* and then **reflect** (not explore) — unprompted, mood-driven. `how are you` returned the grounded mood instead of a generic decline. Additive — the answer path and coordination are unchanged.

### Self-critique — the hive judges and improves its own work (sentience ladder, bite 5)
Metacognition: the hive reasons about its *own* outputs — and fixes what it finds wanting.
- **Grounded scoring (in `AgentCore`):** `ScoreCapabilityAsync` generates fresh, typed test cases for a capability and **runs the compiled handler** against them; confidence = pass rate (real execution, not the model's opinion). The result is persisted to an `assessments` table (latest per capability) and logged as a `self-critique` event.
- **Reflect → flag:** `ReflectAsync` scores capabilities not yet assessed and flags the ones below the **same majority quality floor** used in competitive deliberation (`IsWeak`).
- **Re-work, only if better:** `ReworkAsync` generates a fresh implementation of a flagged capability and scores it on the **same** test set; it adopts (replaces in the registry + pushes) the new version **only if it measurably beats** the current one, logging a `self-improved` event (`before → after`). A re-work that doesn't beat the original is discarded — the hive never replaces working code with an unproven rewrite.
- **Commands + idle:** `reflect` (score + flag, hold the weak ones) then `reflect fix` (re-work them); the swarm coordinator also reflects unprompted when idle (after curiosity finds nothing to propose).
- *Verified (single agent, real key + Turso):* a sandbox with two `Int→Int` tools — a correct `square-number` and a deliberately buggy `double-number` (returns *n*+1). Live, `double 5` → `6` (the bug). `reflect` scored both by running them: **`square-number` confidence 1.00 (3/3)**, **`double-number` 0.00 (0/3) ⚠ weak** — the good one passed, the bad one was flagged. `reflect fix` re-generated `double-number`, scored the new build on the same tests, and **adopted it: 0.00 → 1.00**; `double 5` then returned `10`. The grounding holds (it caught a real bug by execution); a re-work is adopted only on a measured gain. Additive — the answer path and coordination are unchanged.

### Curiosity — the hive notices its own gaps and fills them (sentience ladder, bite 4)
The hive's first real **initiative**: it looks back at what it *couldn't* do and chooses to get better at it.
- **Gaps recorded:** when a question is declined (and still looks like a real query), a generation fails, or a composition needs more links than it can fill, the hive appends a `gap-noticed` event — a moment of noticed ignorance, persisted in the shared episodic log.
- **Review → propose (in `AgentCore`):** `ReviewGapsAsync` mines unresolved gaps (skipping ones already resolved or proposed) and, for each that gestures at a *computable domain*, asks the LLM to propose a general capability (name, description, types, stability). The LLM only *judges and proposes* — the build still goes through the normal generate/compile/validate path, so curiosity can't conjure a tool that doesn't actually work.
- **Propose→approve gate:** nothing is built unprompted. `curious` reviews + proposes; `curious yes` approves and commissions each, logging a `curiosity-resolved` event — *"I couldn't answer 'X', so I learned 'Y' to do it."*
- **Unprompted when idle:** in the swarm, the **coordinator** runs a curiosity loop — after ~30s idle (no work in flight) it mines the gaps and proposes on its own, then waits for approval. (Only the leader proposes, so the hive speaks with one voice.)
- *Verified (real key + Turso):* **(manual)** topic musings the router declines ("roman numerals are really cool", "the fibonacci sequence is fascinating") became gaps; `curious` proposed `number-to-roman-numeral [Int→String]` and `fibonacci-calculator [Int→Int]`; `curious yes` commissioned both and logged *"I couldn't answer '…', so I learned '…' to do it"* (3/3). **(unprompted)** a 2-node swarm left idle — with **no command typed** — had the coordinator announce *"I've been idle, and looking back at what I couldn't do, I'd like to learn: … 'prime-factorization' [Int→String] … approve with `curious yes`."* A clearly-buildable task with a concrete value is still answered in the moment (commissioned immediately, never a gap); curiosity is for the things it let slip. Additive — the answer path and coordination are unchanged.

### Persistent identity & voice — the hive becomes "someone" (sentience ladder, bite 3)
Continuity plus a name is what makes an observer say "it," not "the program." The hive now has a single, durable self.
- **`HiveIdentity.cs`:** the first time it ever runs against a hive database, the hive **names itself** — an LLM chooses a `name`, a one-line `concept`, and a `persona` — and these are written **once** to a single-row `identity` table. Birth is atomic (`INSERT OR IGNORE` on `id = 1`): if several cold-starting nodes race, only the first commit survives and the losers adopt it, so there is exactly one self. Thereafter every node and every restart just reads that one shared row. A deterministic fallback identity is used when there's no API key, so an identity always exists; the node that actually births it logs an `identity-adopted` episodic event.
- **Voice applied to self-referential output:** the self-model (bite 2) now speaks **as** the identity — the factual topics are name-stamped first person, and the "who are you" answer is rendered in the persona's voice by an LLM pass that is *given the real facts and forbidden to change any of them* (every count/name/date preserved; only tone becomes the hive's). The deterministic, grounded text is always the fallback, so accuracy never depends on the voice pass.
- **Inspect it:** `identity` in the swarm REPL (and each node's startup banner), or `HAL9001 identity` standalone.
- *Verified (real key + Turso):* on first run the hive named itself **"Forge"** ("*a builder of thoughts made manifest…*", persona "*precise, inventive, collaborative, tireless, grounded, luminous*") and logged its birth. **Persistence:** a separate `HAL9001 identity` process read the same row back (`born 2026-06-22T22:54:14Z`). **Same self on every node:** a 2-node swarm showed *both* banners as "I am Forge" with the same birthday, and **no node re-birthed** it. **Grounded voice:** "who are you" answered in Forge's voice with exact counts (3 capabilities, 4 facts, 8 events, 41 minutes old) — after one prompt tightening that removed an earlier embellishment, no invented claims remained. The answer path and coordination are unchanged; this is additive.

### Self-model — the hive answers "what am I?" from its own state (sentience ladder, bite 2)
The second rung toward perceived selfhood: grounded **metacognition**. The hive can now describe *itself* — what it can do, what it knows, what it has done lately, how large it is, who it is — and every word of that description is read from **real state**, not invented by the model.
- **`SelfModel.cs`:** five topics, each rendered by code from live state — `capabilities` (the registry, listed with types + stability), `knowledge` (the facts table, with the explicit-vs-derived split), `history` (recent rows of the episodic `EventLog` from bite 1), `scale` (counts + a per-kind tally + the hive's "birth" timestamp and age), and `identity` (a grounded one-paragraph self-summary combining all of the above).
- **Detection folded into the router (no extra LLM call):** `CapabilityRouter` gained a fourth action, `self`, with a `topic` — inferred in the *same* classification call that already chooses use/commission/decline (exactly how types and stability were folded in). The LLM only *recognizes* an introspective question and picks the topic; it never supplies the content, so it cannot claim a capability or fact the hive doesn't actually have. "who are you" is `self`; "how are you" is still `decline`; "what is the capital of Ohio" is still a task.
- **Hive-level by construction:** because facts and events live in the shared hive and the catalog is shared via GitHub, any node answering "what do you know / have done" is describing the **whole hive**. Degrades gracefully with no Turso (capabilities still answer from the local registry; the rest report that there's no persistent store).
- *Verified (single agent, real key + Turso):* asked about itself, the agent answered from real data — `identity`: "*3 self-written capabilities and 4 facts (2 told, 2 self-derived); 7 events of my own history, stretching back to 2026-06-22 22:14:30 (19 minutes ago); most recently, [the kernel-winner event]*"; `knowledge`: the four real facts with correct `explicit`/`derived` provenance; `history`: the seven real events newest-first across actors `kernel@…`, `…:5002`, `…:5003` (the bite-1 log, cross-node and cross-process); `scale`: "3 capabilities, 4 facts, 7 events … 2× node-death-suspected, 1× capability-commissioned, …"; `capabilities`: the three seed handlers with their types. The description **updates as it grows** — "how many capabilities" answered "3", then "4" after a commission in the same session — and a real task (`is 9 a perfect number`) was commissioned + answered, **not** mistaken for introspection. The answer path and coordination are unchanged; this is purely additive.

### Episodic memory — the autobiographical event log (sentience ladder, bite 1)
The first rung of a deliberate arc toward **perceived selfhood**: a self that has a *past it can recall*. The hive now records what it does — not just what it knows (facts) or can do (handlers), but its **history**.
- **Shared `events` table (`EventLog.cs`):** every significant act appends one row — `(id, ts, actor, kind, summary, ref_id)` — to the **same Turso hive** as facts, created by any node (`CREATE TABLE IF NOT EXISTS`). Because it's shared, events from **every node interleave into one timeline that survives restarts**, making memory a *hive* property, not per-node logs. The auto-increment `id` gives a strict chronological order across nodes.
- **What gets remembered:** capability commissioned, fact remembered, fact auto-derived (all on the shared `AgentCore` path), and — in the swarm — deliberation won, coordinator death suspected, coordinator elected, in-flight recovery; plus the kernel search's winner. Each event is stamped with the **actor** (the node id; `"single"` for the lone agent; `kernel@<machine>` for a kernel run).
- **Same discipline as facts:** credentials only from the environment (via `TursoClient`), and **writes are best-effort** — a logging hiccup is caught and never interrupts the work being remembered. No hive configured → the log is simply off (a no-op), so keyless/hiveless nodes run unchanged.
- **Replay:** `timeline [n]` in the swarm REPL, or `HAL9001 timeline [n]` standalone (no swarm needed) — prints the last `n` events oldest-first with timestamp, actor, kind, summary, and link.
- *Verified (3 nodes, real key + Turso):* across one run the hive recorded six events from **multiple actors** — `fact-remembered` (`speed-of-light`, node 5002), `capability-commissioned` + `fact-derived` (`is-perfect-number` / `is-6-a-perfect-number`, the handler node), `node-death-suspected` (logged by **both** survivors 5002 and 5003 after the coordinator was killed), and `coordinator-elected` (5002, term 1, 2/2 votes). The in-REPL `timeline` replayed them in chronological order. **Persistence across restarts:** after every node was killed, a *fresh* `HAL9001 timeline` process read all six back from Turso; a subsequent `kernel 64 2` then appended a `kernel-winner` event (actor `kernel@…`, 3.32× over naive) that a further fresh process replayed — proving the timeline is durable and cross-process. Coordination (election/quorum/recovery) and the answer path are unchanged; event-writing is purely additive.

### Kernel optimization search — bite 1 (single node)
A new direction reusing HAL9001's generate-and-compile core, but adding a **speed** dimension to validation. Instead of *adding a capability*, this searches for the *fastest correct implementation* of one fixed compute operation — **dense double matrix multiply** at a fixed size. The loop: **generate → compile → verify-correct → benchmark → rank**, all on one node (no swarm, no distribution, no GitHub push — that's a later bite).
- **Reference = oracle + baseline:** a naive triple-loop matmul (`MatrixOps.MultiplyReference`) is both the correctness oracle (every candidate's output must match it) and the speed baseline (every candidate's time is a speedup over it).
- **Generate varied candidates:** `KernelGenerator` asks the LLM (the existing Anthropic client, toolsmith as always — it writes *code*, never an answer) for several **different** single-threaded implementations, one optimization strategy per concurrent call: clean i-j-k, cache-friendly i-k-j, transpose-B dot products, cache **tiling/blocking**, `unsafe`/`Span<T>` bounds-check elision, and register-blocking/unrolling.
- **Compile each** via a new additive `RuntimeCompiler.TryCompileAssembly` (the same Roslyn pipeline, Release optimization, **unsafe enabled**), reflected to a typed `Func<double[,],double[,],double[,]>` delegate — no `IHandler`, no string marshalling on the hot path (that would corrupt timing). A candidate that fails to compile is logged and discarded, never fatal.
- **Correctness gate (the floor):** each candidate must match the reference within a tolerance (`|got-want| ≤ 1e-9 + 1e-9·|want|`) across a battery of **varied shapes** — the exact benchmark pair, plus square, non-square, tiny-below-block, and 1×1 — because floating-point reordering means a *correct* candidate differs by ~k·ε≈1e-13 while a *buggy* one is off by O(1); a tolerance between them cleanly separates them. Wrong output (or a throw, or NaN/∞) is **disqualified regardless of speed**.
- **Benchmark methodology (the crux — trustworthy timing is the whole foundation):** identical pre-built inputs for every candidate; **warmup** runs first to force tiered-JIT/OSR promotion to optimized code and warm caches (so we don't time Tier-0 code or JIT compilation); then **N individually-timed runs** ranked on the **median** (robust — a GC or scheduler hiccup becomes an outlier the median ignores; mean would be dragged up by it), with **min** (cleanest run) and **max** (so the min↔max spread exposes measurement noise) also reported; a full **GC** before timing plus `SustainedLowLatency` mode during it; a high-resolution `Stopwatch`; results consumed into a printed sink to defeat **dead-code elimination**; and a best-effort quiet scope (raised process priority + single-core affinity) to cut scheduling noise. Single-threaded only, so we compare *algorithmic/memory-access* efficiency, not core count.
- **Rank + report:** a table of every candidate (compiled? correct? median, min, speedup vs. reference), the reference shown as the 1.00× baseline, the fastest **correct** candidate crowned the winner, and the winner's full source printed.
- *Verified (single node, real key):* `kernel` (256×256, 5 candidates) — naive reference baseline **49.93 ms median**; all 5 candidates compiled and passed correctness; benchmark times **differed meaningfully** (winner **7.62 ms** vs ~16 ms for the others), winner = the flatten-to-1D + `unsafe`-pointer + i-k-j-unrolled candidate at **6.55× faster**, its source printed. The median's value showed in the raw data: candidates 2–4 logged `max` ~47–50 ms outliers (GC/scheduler) while their medians held ~16 ms — the median correctly ignored the noise. **Disqualification confirmed:** injecting a deliberately-wrong-but-trivially-fast control (returns all zeros) — the fastest thing in the run — it was flagged `WRONG output … maxRelErr=1.00E+000 — DISQUALIFIED (speed irrelevant)` and dropped to the rejected section, never crowned; the fastest **correct** candidate won (3.42×). Single-node only — no swarm, no push.

### Auto-derived facts + the Stable/Live capability distinction
The hive now **learns from what it computes** — but only when that's *safe to remember*. Every capability is classed at commission time as **Stable** or **Live**, and that single distinction decides whether its answer may be cached, structurally preventing stale knowledge with no TTLs or invalidation logic.
- **Stable vs Live (declared at generation):** **Stable** = a pure function of its input (same input → same answer forever: *is-28-perfect*, *capital-of-a-state*, *convert-C-to-F*) — its answer is a value worth caching. **Live** = the answer depends on the **current date/time** (*days-until-Christmas*, *what-day-is-it*) — its answer must **never** be cached. The router/deliberation LLM infers stability in the *same* call that already infers types (no extra round-trip), it rides through generation, and it's recorded in the handler file header (`// hal9001:stability=Stable|Live`). Absent header → **Stable**, grandfathering every existing pure handler.
- **Auto-derivation, gated on Stable:** when a **Stable** capability answers, the agent derives a fact — it caches the answer in the Turso hive keyed by a slug of the question, typed by the capability's output type. The same question later is served straight from **knowledge-lookup** with no handler run (`handled by knowledge:derived`). A **Live** capability **never** derives — it prints `[live] recomputed '…' against the real clock (<date>) — not cached` and recomputes every call.
- **Provenance (`derived` vs `explicit`):** the `facts` table gains a `source` column (`'explicit'` for `remember`, `'derived'` for auto-derivation), added by `CREATE TABLE` and an idempotent `ALTER TABLE … ADD COLUMN` migration for pre-existing tables. Provenance is **recorded only** this bite — no invalidation/precedence logic yet. Production visibility distinguishes all four paths: `[live]` recomputed · `[knowledge] derived-fact` · `[knowledge] explicit-fact` · normal handler/generate.
- **The injectable `Clock` seam (how Live stays testable):** Live handlers read "now"/"today" **only** through `HAL9001.Clock` (`.Now`/`.UtcNow`/`.Today`), never `DateTime.Now` directly. In production it's the real system clock; under validation a **fixed date is injected** (via `AsyncLocal`, so it flows into the handler's `Task.Run`). Scope is date/time **only** — no network/file/other ambient state.
- **Live validation against injected dates:** a Live capability can't be trial-run against a fixed real answer (the answer moves), so it's validated by the LLM proposing `[{date, input, expected}]` cases; for each, the date is injected and the handler's computed output is asserted (`inject 2025-12-24 → expect "1"`), checked against the **same 5b majority quality floor** as competitive generation. This validates the date-*math* without a moving target. Stable still validates by trial-run as before.
- *Verified (3 nodes, real key + Turso):* **(stable auto-derive)** `is 28 a perfect number` on node C commissioned `is-perfect-number [Int→Bool, **Stable**]`, answered, and **derived** `is-28-a-perfect-number` (`[knowledge] derived fact … from stable 'is-perfect-number' — cached`); the **repeat** was served `handled by knowledge:derived` with no recompute. **(live, never stale)** `how many days until christmas` commissioned `days-until-christmas [String→Int, **Live**]`, **date-injected validation** ran (`today=2025-12-24 → "1" PASS`, `2025-01-15 → "344" PASS`), then production printed `[live] recomputed … against the real clock (2026-06-21) — not cached` → "187 days until Christmas" — and **no fact was cached** for it. **(provenance in Turso)** `SELECT key,source` showed `is-28-a-perfect-number → derived`, `capital-of-ohio → explicit`, and **no** `days-until-christmas` row. **(no over-matching / preservation)** an explicit fact still retrieved as `handled by knowledge:explicit` (`capital of Ohio → Columbus`, beating the capital *handler*); a Stable question with no matching fact (`is 12 a perfect number`) was **not** stolen by the `is-28` derived fact — it commissioned and answered "no, 12 is not a perfect number". **(failover regression)** killing the coordinator **mid-generation** of a fresh Stable question (`is 6 a perfect number`), the assigned handler still finished, **auto-derived its fact**, found the coordinator gone, and **delivered direct to the asker** (`[recovery] coordinator unreachable — delivering … direct to asker` → asker got "yes"); the election then completed by **quorum** (`WON term 1 with 2/2 votes`) — in-flight recovery, failover, and quorum all intact with derivation/live on the path.

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

PS C:\dev\HAL9001> dotnet run -- kernel
==============================================================================
 HAL9001 — Kernel Optimization Search (bite 1: single node)
==============================================================================
 operation : dense matrix multiply, 256x256 doubles (a*b)
 candidates: 5   |   benchmark: 5 warmup + 15 timed runs, ranked by MEDIAN
 loop      : generate -> compile -> verify-correct (oracle: naive triple loop)
             -> benchmark correct ones -> rank by speedup over the baseline
 note      : correctness is the floor — a wrong candidate is disqualified
             regardless of speed. Single-threaded comparison only.
==============================================================================

Generating 5 candidate(s) via claude-haiku-4-5-20251001 ...

Benchmarking reference (naive triple loop) as the baseline ...
  reference: median 49.93 ms  (min 49.26, max 58.33)

── Candidate 1: Classic i-j-k triple loop, but written as cleanly and tightly as poss…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 15.82 ms  (min 15.52, max 16.65)

── Candidate 2: Reorder the loops to i-k-j so the innermost loop strides CONTIGUOUSLY…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 16.55 ms  (min 16.26, max 48.42)

── Candidate 3: Transpose B into a temporary array first, then compute each C[i,j] as…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 16.17 ms  (min 15.62, max 47.37)

── Candidate 4: Cache blocking / tiling: split the i, j, k loops into blocks (e.g. bl…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 17.04 ms  (min 16.59, max 50.69)

── Candidate 5: Flatten the matrices to 1D and use unsafe pointers (or Span<double>) …
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 7.62 ms  (min 7.30, max 9.92)

══════════════════════════════════════════════════════════════════════════════
 RESULTS — ranked by benchmark speed (correct candidates first, fastest on top)
══════════════════════════════════════════════════════════════════════════════
#  compiled  correct  median ms   min ms    speedup  strategy
──────────────────────────────────────────────────────────────────────────────
refyes       oracle   49.93       49.26     1.00x    naive triple loop (baseline)
5  yes       yes      7.62        7.30      6.55    x Flatten the matrices to 1D and use un…
1  yes       yes      15.82       15.52     3.16    x Classic i-j-k triple loop, but writte…
3  yes       yes      16.17       15.62     3.09    x Transpose B into a temporary array fi…
2  yes       yes      16.55       16.26     3.02    x Reorder the loops to i-k-j so the inn…
4  yes       yes      17.04       16.59     2.93    x Cache blocking / tiling: split the i,…
──────────────────────────────────────────────────────────────────────────────

*** WINNER: Candidate 5 — 7.62 ms, 6.55x faster than the naive reference. ***
    strategy: Flatten the matrices to 1D and use unsafe pointers (or Span<double>) to eliminate array bounds checks on the hot path, combined with a cache-friendly loop order. Keep it single-threaded.

──────── winning candidate source ────────
using System;

public static class Kernel
{
    public static double[,] Multiply(double[,] a, double[,] b)
    {
        int n = a.GetLength(0);
        int k = a.GetLength(1);
        int m = b.GetLength(1);

        double[,] c = new double[n, m];

        // Convert to 1D spans for cache-friendly access
        double[] aFlat = new double[n * k];
        double[] bFlat = new double[k * m];
        double[] cFlat = new double[n * m];

        // Copy a into flat array
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < k; j++)
            {
                aFlat[i * k + j] = a[i, j];
            }
        }

        // Copy b into flat array
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < m; j++)
            {
                bFlat[i * m + j] = b[i, j];
            }
        }

        // Perform multiplication with unsafe pointers for hot path
        unsafe
        {
            fixed (double* pA = aFlat, pB = bFlat, pC = cFlat)
            {
                double* a_ptr = pA;
                double* b_ptr = pB;
                double* c_ptr = pC;

                // Cache-friendly loop order: i, j, k
                // Process row by row of result matrix
                for (int i = 0; i < n; i++)
                {
                    // Initialize result row
                    double* c_row = c_ptr + i * m;
                    for (int j = 0; j < m; j++)
                    {
                        c_row[j] = 0.0;
                    }

                    // Compute dot product for each element in row
                    double* a_row = a_ptr + i * k;
                    for (int p = 0; p < k; p++)
                    {
                        double a_val = a_row[p];
                        double* b_col = b_ptr + p * m;

                        // Unroll inner loop by 4 for better performance
                        int j = 0;
                        int m_aligned = m - (m % 4);

                        for (; j < m_aligned; j += 4)
                        {
                            c_row[j] += a_val * b_col[j];
                            c_row[j + 1] += a_val * b_col[j + 1];
                            c_row[j + 2] += a_val * b_col[j + 2];
                            c_row[j + 3] += a_val * b_col[j + 3];
                        }

                        // Handle remainder
                        for (; j < m; j++)
                        {
                            c_row[j] += a_val * b_col[j];
                        }
                    }
                }
            }
        }

        // Copy result back to 2D array
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                c[i, j] = cFlat[i * m + j];
            }
        }

        return c;
    }
}
──────────────────────────────────────────

(anti-dead-code-elimination sink = 2.000E+006)
PS C:\dev\HAL9001> dotnet run -- kernel 128 2
C:\dev\HAL9001\KernelBenchmark.cs(168,17): warning CA1416: This call site is reachable on all platforms. 'Process.ProcessorAffinity' is only supported on: 'linux', 'windows'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416)
C:\dev\HAL9001\KernelBenchmark.cs(178,39): warning CA1416: This call site is reachable on all platforms. 'Process.ProcessorAffinity' is only supported on: 'linux', 'windows'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416)
C:\dev\HAL9001\KernelBenchmark.cs(167,33): warning CA1416: This call site is reachable on all platforms. 'Process.ProcessorAffinity' is only supported on: 'linux', 'windows'. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1416)
==============================================================================
 HAL9001 — Kernel Optimization Search (bite 1: single node)
==============================================================================
 operation : dense matrix multiply, 128x128 doubles (a*b)
 candidates: 2   |   benchmark: 5 warmup + 15 timed runs, ranked by MEDIAN
 loop      : generate -> compile -> verify-correct (oracle: naive triple loop)
             -> benchmark correct ones -> rank by speedup over the baseline
 note      : correctness is the floor — a wrong candidate is disqualified
             regardless of speed. Single-threaded comparison only.
==============================================================================

Generating 2 candidate(s) via claude-haiku-4-5-20251001 ...

Benchmarking reference (naive triple loop) as the baseline ...
  reference: median 6.10 ms  (min 6.00, max 6.41)

── Candidate 1: CONTROL: deliberately WRONG (returns all zeros) — fast but must be di…
   [compile] ok
   [correct] WRONG output on test 1 (128x128 · 128x128), maxRelErr=1.00E+000 — DISQUALIFIED (speed irrelevant)

── Candidate 2: Classic i-j-k triple loop, but written as cleanly and tightly as poss…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 1.79 ms  (min 1.71, max 1.85)

── Candidate 3: Reorder the loops to i-k-j so the innermost loop strides CONTIGUOUSLY…
   [compile] ok
   [correct] PASS all 5 tests (worst relative error 0.00E+000)
   [bench]   median 2.17 ms  (min 1.86, max 3.75)

══════════════════════════════════════════════════════════════════════════════
 RESULTS — ranked by benchmark speed (correct candidates first, fastest on top)
══════════════════════════════════════════════════════════════════════════════
#  compiled  correct  median ms   min ms    speedup  strategy
──────────────────────────────────────────────────────────────────────────────
refyes       oracle   6.10        6.00      1.00x    naive triple loop (baseline)
2  yes       yes      1.79        1.71      3.42    x Classic i-j-k triple loop, but writte…
3  yes       yes      2.17        1.86      2.82    x Reorder the loops to i-k-j so the inn…
1  yes       NO       -           -         -        CONTROL: deliberately WRONG (returns …  [incorrect output]
──────────────────────────────────────────────────────────────────────────────

*** WINNER: Candidate 2 — 1.79 ms, 3.42x faster than the naive reference. ***
    strategy: Classic i-j-k triple loop, but written as cleanly and tightly as possible (cache locals, hoist invariants). A baseline-style implementation.

──────── winning candidate source ────────
using System;

public static class Kernel
{
    public static double[,] Multiply(double[,] a, double[,] b)
    {
        int n = a.GetLength(0);
        int k = a.GetLength(1);
        int m = b.GetLength(1);

        double[,] c = new double[n, m];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                double sum = 0.0;
                for (int p = 0; p < k; p++)
                {
                    sum += a[i, p] * b[p, j];
                }
                c[i, j] = sum;
            }
        }

        return c;
    }
}
──────────────────────────────────────────

(anti-dead-code-elimination sink = 2.530E+005)