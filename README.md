# Nova

Nova is a voice-first desktop assistant for Windows, built on Claude. The idea started with E.V. from *Spider-Man: Brand New Day* — an AI that doesn't just answer when spoken to, but notices what you're doing and offers to help. I wanted to see how much of that was actually buildable with what's available today, not just as a chatbot with a microphone bolted on, but something that watches your screen, acts on real apps, and remembers you from one session to the next.

You talk to it, it talks back — fully local speech-to-text and text-to-speech, so nothing you say leaves your machine unless a task actually needs the internet. It reads native apps and the browser through Windows UI Automation and Playwright rather than screenshots, so it's reading structured data, not guessing from pixels. It keeps a real memory across restarts (and gets smarter about *how* it remembers things, not just that it does). And it can write, compile, and run its own small tools at runtime when it hits something it doesn't have a tool for yet, with a self-repair loop if one of those tools starts misbehaving.

### A few things I'm actually proud of

**The permission model.** My first pass at "ask before doing anything risky" ended up asking three separate times for one task — can I read this, can I edit this, can I send this — which felt like filling out a form, not talking to an assistant. It's down to two checkpoints now: one spoken authorization that only fires when Nova brings something up on her own (not when you directly ask for it — you already gave permission by asking), and one stricter, click-only popup review that fires for anything genuinely irreversible, no matter how the task started. A direct request like "fix the typos in this email and send it" now runs with zero interruption right up until the actual send, where you get one real look at what's about to go out.

**Self-repair.** Every tool Nova builds for herself lives in its own isolated, git-versioned project — never touching her own core logic. If one starts failing three times in a row on the same task, she automatically reverts it to the last version that worked, rebuilds it, and just mentions it in passing rather than making a whole announcement out of it.

**No wake word, one hotkey.** Earlier versions had a whole ladder of "comfort levels" you'd switch between by voice, including a fully-ambient always-listening mode. I scrapped it — that's exactly the kind of unsolicited-interruption behavior people hated about Clippy. Now there's just awake and asleep, and the only way to wake her back up is a hotkey, never a spoken word she might mishear out of context.

Full write-up of how all this fits together: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Things I tried and specifically didn't do, and why: [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md).

## The overlay

Nova runs as a small floating overlay rather than a console window, with three interchangeable skins — same live state (listening, speaking, asleep, what she's currently doing), three completely different looks. Cycle between them anytime with the switcher button.

| ARC | WEB | AURA |
|---|---|---|
| ![ARC skin](docs/screenshots/arc.png) | ![WEB skin](docs/screenshots/web.png) | ![AURA skin](docs/screenshots/aura.png) |
| Hologram-globe HUD, cyan theme | Retro pixel-art tracker | Soft pastel companion |

## What's next

- Full UI customization — position, theming, layout, beyond just the three built-in skins
- Figma and Blender integrations, built the same way as any other self-contained tool, driving each app's own scripting API rather than generating content from scratch
- Proactive inbox push-watching instead of polling
- 3D-printable model generation for parametric shapes (brackets, mounts, enclosures) via OpenSCAD
- A thin phone client eventually, with the PC doing all the actual reasoning

## Getting started

Windows only — it leans on Windows UI Automation and native `cmd.exe` integration. Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/wasmiester/NOVA.git
cd NOVA
cp secrets/.env.example secrets/.env
# edit secrets/.env and set ANTHROPIC_API_KEY
dotnet run
```

First run downloads about 400MB of local models (Whisper for speech-to-text, Silero for voice-activity-detection, Kokoro for text-to-speech) — after that, everything runs offline. Gmail, Calendar, Docs, Drive, Sheets, and Slides are optional and can be connected later from the in-app credentials popup, no restart required (see `secrets/.env.example` for the Google Cloud setup steps).
