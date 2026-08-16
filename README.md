# Nova

Nova is a voice-first desktop assistant for Windows, built on Claude, inspired by E.V. from *Spider-Man: Brand New Day* — an AI that chains naturally from task to task and notices what you're doing rather than waiting to be asked. It talks and listens with fully local speech-to-text/text-to-speech, reads and acts on native apps and the browser via Windows UI Automation and Playwright, remembers you across sessions, and can extend and repair her own capabilities.

A few design decisions worth calling out:

**The permission model (Gate 1 / Gate 2).** Early on, a naive "ask before every state-changing action" design read as a bureaucratic form — can I read this, can I edit this, can I send this. Nova collapses that to two real checkpoints: **Gate 1** is task-level authorization, and only fires when Nova is acting on her *own* initiative (an unprompted ambient suggestion) — a direct request already carries its own authorization in the asking. **Gate 2** is a stricter, click-only review that fires once per task for anything irreversible or leaving the machine, regardless of how the task started. One ask, one check, not three prompts in a row.

**Self-repair.** Nova can write, compile, and run her own small isolated tools at runtime — each in its own assembly, git-versioned, never touching her own core safety logic. Three failures in a row on the same task triggers an automatic revert to the last working version, rebuilt and re-registered, with no separate canned announcement — just folded into what she says next.

**Engaged/asleep, not named comfort tiers.** An earlier 3-tier mode ladder (including a fully-ambient "Autonomous" mode) was scrapped in favor of a single mode with one flag: awake or asleep, woken only by a hotkey — deliberately avoiding the unsolicited-interruption failure mode people associate with Clippy.

Full design reasoning: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Rejected approaches and why: [docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md).

## Getting started

Windows only (Windows UI Automation, native cmd.exe integration). Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone <this repo>
cd NOVA
cp secrets/.env.example secrets/.env
# edit secrets/.env and set ANTHROPIC_API_KEY
dotnet run
```

First run downloads ~400MB of local models (Whisper speech-to-text, Silero voice-activity-detection, Kokoro text-to-speech) — everything runs offline after that. Gmail/Calendar/Docs/Drive/Sheets/Slides are optional; connect them later from the in-app credentials popup, no restart needed (see `secrets/.env.example` for the Google Cloud setup steps).
