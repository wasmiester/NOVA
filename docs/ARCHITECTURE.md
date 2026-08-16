# Nova — architecture

*Inspired by E.V. (Spider-Man: Brand New Day) — a proactive, voice-first assistant that chains naturally from task to task, notices what you're doing, and offers to help rather than waiting to be asked.*

**Nova** — **N**otices, **O**ffers, **V**erifies, **A**cts. The acronym doubles as the permission model below. "Notices" covers two different things: reasoning about the natural next step in a task already underway (no gate needed — you're already mid-conversation), and ambient sensing of files/screen/terminal when nothing's been asked (gated, since there's real judgment about whether to interrupt).

## Contents

- [Tech stack](#tech-stack)
- [Engaged/asleep state model](#engagedasleep-state-model)
- [Permission model](#permission-model--one-ask-one-check)
- [Self-contained tools & self-repair](#self-contained-tools--self-repair)
- [Memory & context strategy](#memory--context-strategy)
- [Google Workspace integration](#google-workspace-integration)
- [Overlay UI](#overlay-ui)
- [What's built / what's next](#whats-built--whats-next)

---

## Tech stack

| Layer | Choice | Why |
|---|---|---|
| Language / app shell | C#/.NET (`net10.0-windows`) | Native Windows automation (UI Automation), official Anthropic SDK exists for C# |
| Claude access | Official `Anthropic` C# SDK | First-party, `AnthropicClient`/`Messages.CreateStreaming` |
| Reasoning core | Claude Sonnet 5 | Full agent loop, tool use, task execution, next-step reasoning |
| Ambient "worth surfacing?" gate | Claude Haiku 4.5 | Cheap/fast, runs constantly without racking up cost — only for idle/ambient triggers, never in-conversation chaining |
| Speech-to-text | Whisper, local, via Whisper.net | No Python glue, runs on CPU, keeps raw audio on-device |
| Text-to-speech | Kokoro-82M, local, via KokoroSharp | Apache 2.0, CPU-friendly, built-in streaming |
| Screen reading (native apps) | Windows UI Automation | Structured control data straight from the OS — not vision, near-instant, no GPU |
| Screen reading (browser) | Playwright for .NET | Reads the DOM directly — reliable form-filling without guessing from pixels |
| Ambient triggers | `FileSystemWatcher`, a pty-wrapped terminal relay | Structured text, cheap to run continuously |
| Persistent memory | SQLite + brute-force cosine similarity, Letta-style tool-based access | Nova calls `save_memory()`/`search_memory()` herself and decides what's worth keeping |
| UI | Avalonia floating overlay, 3 selectable skins (ARC/WEB/AURA) | Software-rendered, DPI-aware, reflects live state (listening/speaking/asleep) |

## Engaged/asleep state model

Nova has two activation modes, not a ladder of named "comfort levels" (an earlier 3-tier design — see [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md) for why it was scrapped), switched by voice ("switch to key bind mode") or the overlay's mode button:

- **Prompted** — the default. Starts engaged: responds to any speech immediately, no hotkey needed.
- **Key Bind** — starts asleep. Nothing is transcribed or reacted to until the hotkey is pressed, specifically so ambient conversation near the machine can never accidentally trigger a response.

Orthogonal to the mode itself is the engaged/asleep flag it defaults into. Once engaged, both modes behave identically — full listening, reasoning about the natural next step once a task finishes, proactively watching files/terminal/screen contextually rather than constantly — until a spoken sleep phrase, the overlay's sleep button, or 3 hours of system-wide AFK idle (`GetLastInputInfo` polled once a minute) puts Nova back to sleep. While asleep, in either mode, Nova doesn't transcribe or react to speech at all — no wake word, nothing sent to Claude — except email/calendar, the deliberate exceptions that keep running regardless. The **hotkey is the only way back to engaged**, in both modes; switching modes itself also immediately applies that mode's default (switching to Prompted wakes Nova up, switching to Key Bind puts her to sleep).

Screen/window reading is contextual rather than always-on: `read_screen`/`interact_desktop` take an optional window-title match so Claude can target a specific open window — including minimized or behind others — without disturbing the foreground. Interacting only brings a window forward when a control lacks a direct UI Automation pattern and has to fall back to a real simulated click/keystroke, which needs actual OS focus; the pattern-based path never touches the screen at all.

## Permission model — one ask, one check

Not three separate prompts in a row ("can I read this" → "can I edit this" → "can I send this") — that breaks the assistant feel. Reading is always free; everything else runs through at most two gates:

- **Sensing — always free, not a gate at all.** Reading files, terminal output, screen content. This is what makes ambient mode possible in the first place.
- **Gate 1 — task authorization, only for what Nova initiates herself.** Fires *only* when a task originated from an ambient trigger (Nova noticing something and wanting to act on it, not just mention it) — a request the user actually asked for already carries its own authorization and just runs, no matter how many gated calls it involves. One ask, spoken, accepts a yes.
- **Gate 2 — review checkpoint, appears once, exactly where it matters.** Fires independently of Gate 1, for anything irreversible, leaving the machine, or untrusted for the first time (sending, submitting, deleting, posting, a self-authored tool's first run). Confirmation is **click-only** via a popup on the overlay — voice can no longer authorize it, since this is the tier where a misheard word actually matters.

**Worked example** — "Fix the typos in this email and send it": no pause at all while Nova reads, drafts, and corrects (you already asked for this) → one review (shows the corrected email) → sends on your go-ahead.

**Asymmetry**: reverting a broken self-contained tool to a previous, already-approved version never needs a fresh Gate 2 — it's not introducing anything new, just retreating to a state that was already reviewed once. Advancing to something new always needs approval; retreating to safety never does.

### The classification rubric

Four tiers, applied consistently rather than decided case-by-case:

1. **Free** — reads or opens something, no state change.
2. **Free** — changes something, but it's Nova's own local bookkeeping, or inert data a human still has to explicitly submit. A "read" with a real side effect (marks something read, consumes a quota) doesn't qualify just because it's framed as a lookup.
3. **Gate 1** — a real external change that's easily undone *in the normal course of the same workflow* — not "theoretically reconstructable with enough effort."
4. **Gate 2** — three distinct reasons, same click-only treatment: **(a)** leaves the machine, **(b)** not realistically reversible, **(c)** the first time something new and unreviewed is being trusted.

Two considerations sit orthogonal to the tiers, and can push something up a level regardless: **cost** (a free-to-run tool call can still cost real money per use — self-contained tools self-declare `uses_paid_api`, disclosed at both the build-ask and the first-run review) and **reaching a third party** (an action reversible for you can still be irreversible for someone else — a calendar event with guests doesn't un-send the invite email just because you delete the event).

**Standing policy on file operations**: read/lookup/create is free; edit (overwrite), delete, download, or run anything capable of those requires Gate 2, regardless of implementation — a future Recycle-Bin-routed delete is still Gate 2 even though it's technically weaker than a permanent one, since the review shouldn't depend on implementation details the user can't see. Every overwrite is recorded first (`FileEditHistory`) so it can be undone with `revert_file_edit`.

**`run_command`** is unconditionally Gate 2 (arbitrary shell text is too open-ended to trust a heuristic as the sole gate) — a separate classifier still runs to shape *what* the review says (a specific "looks like it could edit/delete/download" reason vs. a plain "review this"), not whether it fires. **`browser_click`/`interact_desktop`** stay Gate 1 only, since their click surface is a structural allowlist (navigational clicks — pagination, expand/collapse, close/dismiss — only), a hard block rather than a heuristic guess.

**Bulk actions** state real scope rather than getting a mechanical re-confirmation per item: one Gate 1 "yes" covers the whole task, so the ask groups same-tool calls together and, past a small threshold, summarizes as "fileA, fileB, and 45 more like that (47 total)" instead of enumerating each one — honest about scale without reintroducing prompt-per-item friction. A genuinely severe bulk action is still caught by Gate 2's own per-round review regardless of how Gate 1 phrased it.

## Self-contained tools & self-repair

Nova can build, run, and self-repair her own small, isolated, git-versioned capabilities at runtime — no manifest/install ceremony, no marketplace framing.

- **Isolation is the safety boundary, not size.** A tool lives in its own compiled assembly and never touches Nova's own core control/safety logic (`NovaAssistant.cs`, the gates, the tool catalog) — it can fail or revert independently without risking anything else. Nova modifying her *own* core codebase autonomously is explicitly out of scope; an agent editing the code that enforces its own guardrails is a well-known hard failure mode.
- **Contract**: a tiny shared project with one interface, `INovaTool.ExecuteAsync(input, cancellationToken) -> string`, referenced by both the host and every generated tool project across an `AssemblyLoadContext` boundary.
- **`build_tool`** writes the full C# source, compiles it, and only registers it if the build actually succeeds — a failed build returns the compiler output to fix and retry.
- **`run_tool`** loads the compiled `.dll` into a fresh, collectible `AssemblyLoadContext`, invokes it once, and unloads immediately — every call, not just the first, with the unload actually verified (a `WeakReference` + bounded GC-collect loop) rather than trusted blindly.
- **Git-versioned**: its own separate local git repo, decoupled from the main app. Every successful build is a commit; nothing is force-reset — a revert is always a new commit restoring older content.
- **Self-repair**: three failures in a row *within the same task* triggers an automatic revert to the previous git version, rebuilds it, and updates the registry — folded into the tool result so Claude's own next reply reports it in her own words, not a separate canned announcement.
- **Free-API-first, paid-API-disclosed**: Claude self-declares `uses_paid_api` only after confirming no free alternative covers the need, disclosed at both the build-ask and the first-run review.

## Memory & context strategy

- Persistent memory lives locally in SQLite, outside the model's context entirely — retrieval is brute-force cosine similarity over locally-computed embeddings (not a native vector-search extension; see [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md) for why).
- **Tool-based, self-directed access (Letta-style)**: Nova calls `save_memory()`/`search_memory()` herself and decides what's worth keeping.
- **Hybrid retrieval**: keyword/tag matching plus semantic search. Every memory carries a scope tag — `durable` (broad/forever facts), `style` (how the user wants to be treated/collaborated with, tracked separately from plain facts), or `task:<name>` (scoped to one task, not a rule to reuse elsewhere).
- **Reinforcement over duplication**: a save that's near-identical to an existing memory (cosine similarity ≥ 0.75) strengthens/updates that row instead of creating a disconnected duplicate.
- **Conversation compaction**: a completed task is archived to a separate store, with a short Haiku-written recap riding forward into the next task's first message instead of the full transcript — full detail is still recoverable via `search_conversation_history` if actually needed.
- **1-hour prompt cache tier**: Nova's usage is bursty (a task, then idle, then another task later); a 5-minute cache window meant most turns paid a full cache-write instead of a cheap cache-read.

## Google Workspace integration

Gmail, Calendar, Docs, Drive, Sheets, and Slides, each via the official `Google.Apis.*` .NET client libraries. Reading is free; writing is Gate 1 (real but easily-undone). Doc/Slides text replacement matches on the text itself (the APIs' own find/replace request), not a character index — an index shifts as content changes and is easy to get subtly wrong against a document Nova only ever saw as extracted plain text. Sheets editing is positional (an A1 range) since that's how a spreadsheet's own UI addresses cells. OAuth scopes are least-privilege where the API allows it (Drive: read anything visible, but only ever write what Nova herself creates/uploads). A credentials popup on the overlay handles first-time connection with no restart required, and proactive watchers (new mail, upcoming calendar events within a configurable lead time) keep running as one of the few things not suppressed while asleep.

## Overlay UI

A transparent, borderless, always-on-top Avalonia window on its own dedicated thread, three selectable skins (a gold/cyan hologram-globe HUD, a retro pixel-art tracker, and a soft pastel companion), all rendering the same live state — listening/speaking/asleep, current activity, a transcript — polled from the assistant roughly 8 times a second. Gate 2 reviews and the Google-credentials prompt are skin-styled popups layered over whichever skin is active, not separate windows.

## What's built / what's next

**Shipped**: local voice loop (Whisper + Kokoro), UI Automation + Playwright screen reading, SQLite memory, the Gate 1/Gate 2 permission model, in-conversation task chaining, ambient file/terminal watching, the full Google Workspace suite, self-contained tools with self-repair, the Avalonia overlay with three skins, the engaged/asleep redesign (multi-window screen reading, AFK auto-sleep, calendar reminders, a Nova-adjustable settings store), general error self-healing surfacing, file/folder deletion (Recycle Bin-routed), and a memory system that reinforces rather than just accumulates.

**Planned**: full UI customization and theming (v4); Figma/Blender integration as self-contained tools, proactive inbox push-watching, 3D-printable model generation via OpenSCAD (v5); a thin phone client with the PC doing the actual reasoning, plus a vision-based screen-reading fallback for non-accessible apps (v6). Also still open: periodic memory consolidation (folding related memories into a richer picture, rather than just reinforcing near-duplicates) and a "want to look or ignore" loop for the save-time-conservatism side of memory curation.
