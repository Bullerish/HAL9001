# HAL 9001 — UI refresh handoff

Brief for a designer picking up **https://hal9001.io**. Everything below is about the *public* page.

---

## 0. What this thing actually is (read this first)

HAL 9001 is a real, running AI agent that writes its own C# code, compiles it, proves it correct, and
races to find faster matrix-multiplication algorithms. It has been alive ~33 days. **Every number on the
page is read live from its shared memory.** Nothing is mocked, seeded, or illustrative.

That fact is the entire product. The page's job is: *make a stranger believe this is real in under ten
seconds, then let them watch it think.* Every design decision should be judged against that.

The aesthetic is deliberate — HAL 9000 from *2001*: black field, red eye, amber/red instrument text, a
green phosphor CRT for the machine's own output. **Keep it.** This is not a candidate for a generic SaaS
restyle. The brief is to make the existing world *legible*, not to replace it.

**Voice:** cold, precise, first-person, slightly unsettling. Never cute. Never exclamation marks.

---

## 1. Layout as it stands

Single scrolling column, ~1900px content width, dark. Top to bottom:

| # | Section | Purpose |
|---|---|---|
| 1 | **Identity block** (left) — eye, "HAL 9001", self-description, Prime Directive, status pills, tokens, sound, GitHub | who/what, and current state |
| 2 | **Green CRT** (right) — the newest code HAL wrote, typed on char-by-char, plus a live status ticker | the "it's thinking right now" surface |
| 3 | **matrices being worked** | the algorithm search, live |
| 4 | **DIRECT HAL — choose an action** | 11 cards; 3 free, 8 cost a token |
| 5 | **Four headline stats** | live nodes / records / life events / discoveries |
| 6 | **WHAT HAL HAS GROWN INTO** | 12 lifetime counters |
| 7 | **HOW YOU KNOW THIS IS REAL** | 4 trust claims |
| 8 | **FUNCTIONS HAL HAS WRITTEN** | catalogue, each links to real source |
| 9 | **SIZE LADDER** | the matrix sizes it is climbing |
| 10 | **CHAMPIONS / SELF-SET GOALS** | best results; its own current intention |
| 11 | **TRANSMISSIONS** | visitor Q&A history |
| 12 | **VISITOR ACTIVITY** | anonymised recent events |
| 13 | **LATEST JOURNAL** | HAL's own reflective writing |

---

## 2. Problems worth fixing, in priority order

### P1 — The hierarchy is inverted
The most convincing thing on the page is the **journal** (§13) — genuinely specific, self-aware writing
that references real internal state. It is *dead last*, below the fold by a mile. The least convincing
thing — repetitive Q&A (§11) — occupies far more vertical space, directly above it.

**Ask:** promote a single journal excerpt near the top. Demote/collapse transmissions.

### P2 — Nothing tells you what to do
There is no call to action above the fold, no explanation of what a "token" is or why you'd spend one,
and no visible path to *fund* HAL — even though funding is what makes it think. A first-time visitor sees
instruments and no door.

**Ask:** one clear primary action above the fold. Explain tokens in one sentence at the point of use.

### P3 — Twelve counters is not a hierarchy
§5 and §6 are 16 numbers in near-identical treatment, and two of them (**records set**, **discoveries**)
are duplicated verbatim between the two sections. Everything is emphasised, so nothing is.

Also: **"280 nodes spawned"** is an artefact of a since-fixed runaway spawn bug, not an achievement —
it inflates a vanity metric and should probably be dropped. And **"4,098 journal entries / 8,192 thoughts
shared"** dwarf **"53 tools invented"**, which honestly communicates "this thing mostly talks to itself."

**Ask:** pick 3–4 hero numbers, demote the rest to a compact list, remove duplicates.

### P4 — "0 DISCOVERIES" reads as failure
It is actually a *rigour* claim: HAL only calls something a discovery if it beats the best result known
to humanity, and it is honest enough not to claim one. Right now it looks like a broken counter, and it
appears twice. The explanation is hidden behind a small ⓘ.

**Ask:** make the zero legible as integrity. Something like *"0 discoveries — HAL will not claim one
until it beats humanity's best. It is currently 4 multiplications away at 3×3."*

### P5 — CHAMPIONS shows "3×3 · 27 muls · 1.00×"
A 1.00× "champion" looks like a bug. It means "no better than the naive method" — which is true and
interesting (3×3 is an open mathematical problem), but the table can't express *open vs solved*.

**Ask:** a state per row — solved / best-known / open — instead of a bare multiplier.

### P6 — SIZE LADDER is unreadable without context
`2✓ 3✓ 4✓ 5 6 7 8✓ 16✓ 32✓ 64 128 256● 512 1024 2048`. A visitor cannot tell what ✓ means, what ● means,
why some are skipped, or why bigger is meaningful. Note 5/6/7 are *new* and legitimately un-raced.

**Ask:** a legend, and a one-line "what am I looking at".

### P7 — Repetition in the live surfaces
Transmissions repeat the same question 3–4 times (visitors click the same free button). Visitor activity
shows *"HAL beat its own speed record"* three times identically. Both need de-duplication or grouping
(*"beat its own record ×3"*).

### P8 — Choice grid is flat
11 cards, near-identical, 8 of them literally "HAL writes the code itself — 1 TOKEN". Free vs paid is a
small label. The 8 "invent a X tool" cards are one action with a parameter, not eight actions.

**Ask:** separate free actions from paid; consider one "invent a tool" card with a topic selector.

### P9 — Mobile is unaddressed
Everything above assumes a wide desktop. The CRT is a fixed-height pane of monospace; the stat grids are
wide; the choice grid is 5-across. Assume it is currently broken on a phone and needs a real pass.

### P10 — Accessibility
Green `#33cc44` on near-black is fine; **dim red/amber body text on black is not** — several passages sit
near or below 4.5:1. There is a `prefers-reduced-motion` path already (typewriter honours it) — extend
that thinking to the eye glow and CRT flicker. Monospace at 12–13px for long prose (the journal) is hard
to read.

---

## 3. Fixed just now (don't re-report)

- **Function catalogue titles were garbage.** Names were extracted from the first pair of single quotes
  in the event text, but the sentence opens *"I couldn't answer …"* — so the apostrophe in *"couldn't"*
  became the opening quote. Cards read `t answer "Input: 360 → Output: …", so I learned` instead of
  `prime-factorization`. This also broke every **source ↗** link (built from the mangled name) and the
  de-duplication, so the "52 written" count was wrong too. Now parsed as the quoted token before the
  `[Type→Type]` bracket.
- **CRT status line was clipped** mid-word at the right edge (`… · last act:`) — the pane is
  `white-space:pre; overflow:hidden`. It now has its own wrapping status bar.

---

## 4. Hard constraints — please respect these

1. **Never invent or placeholder a number.** Every figure is live from `/api/state`, `/api/growth`,
   `/api/functions`, `/api/live`, `/api/matrix`. If a value is missing, show it as missing. The page's
   only real asset is that it does not lie.
2. **The honest-but-unflattering numbers stay.** `0 discoveries`, `1.00×`, `LLM idle` are load-bearing
   truth. Reframe them; do not hide them.
3. **Keep the HAL 9000 identity** — black, red eye, green CRT, instrument typography.
4. **Don't add tracking, external fonts, CDNs, or analytics.** The page is served by a single self-
   contained C# process behind Caddy, with a strict "everything is public and inspectable" posture.
5. The UI is **embedded as a string in `Dashboard.cs`** (HTML + CSS + vanilla JS, no build step, no
   framework). Deliver changes as HTML/CSS/JS that can live there, or flag clearly if you need a
   different structure.

---

## 5. What "good" looks like

A stranger lands, and within ten seconds:
1. understands a machine is doing real work right now, unsupervised;
2. sees one specific, checkable piece of evidence (a function it wrote, linked to real source);
3. understands it is *paused for lack of funding* and that they can change that;
4. wants to keep watching.

Today the page achieves (1) for a patient technical reader, half of (2), and neither (3) nor (4).

---

## 6. Useful raw material

- Live JSON, no auth: `/api/state`, `/api/growth`, `/api/functions`, `/api/live`, `/api/matrix`,
  `/api/choices`, `/api/wallet`
- Source: https://github.com/Bullerish/HAL9001 (`Dashboard.cs` holds the entire front end)
- The journal is the best copy on the site and was written by HAL — mine it for voice.
