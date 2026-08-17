# Nova — design decisions

What Nova doesn't do, and why — the rejected approaches are often better evidence of engineering judgment than the ones that shipped.

## OpenClaw as a foundation

OpenClaw (formerly Clawdbot) is a large, fast-growing, MIT-licensed open-source agent with system access, browser control, and a permission/skills model genuinely close in spirit to Nova. Rejected as a foundation for two reasons: it's TypeScript, not the C# this project is specifically built in, and building on top of an already-massive active project would undercut the actual point of differentiation — Nova's voice-first, proactive layer. Kept as a read-only architecture reference instead. (The abandoned "Clawdbot" name also attracted impersonation/scam activity after the rename — part of why Nova took an original name rather than something closer to its own inspiration.)

## Kafka

Considered for ambient-trigger ingestion and for a future multi-device sync link — overkill for both. An in-process queue (.NET Channels) covers ambient ingestion; a direct WebSocket/gRPC connection covers phone↔PC. Right-sized beats resume-shaped.

## Cloud STT/TTS as the permanent plan

Originally scoped cloud-first (a hosted speech-to-text API + a hosted voice API), largely on the assumption that local text-to-speech carried a real quality tax. Reversed after checking current local options — Kokoro-82M (Apache 2.0, CPU-friendly) and Chatterbox (MIT, beat a well-known commercial voice API in a blind listening test) show local no longer means a quality compromise. Reinforced by a broader principle: "always listening" is a trust-sensitive feature for any ambient assistant, and keeping raw audio on-device matters more here than it would for an occasional-use tool.

## Vision-based screen reading as the default

Considered and deliberately deprioritized. Windows UI Automation (native apps) and Playwright (browser/DOM) read structured data directly from the OS or the page — no ML inference, near-instant, no GPU. A vision pipeline (screenshot → a vision-capable model) is a fundamentally heavier path: different ingestion, worse token economics, harder context management — and almost nothing in Nova's actual use cases (coding tools, browser forms) needs it. Kept as a fallback for the rare app with no accessibility tree at all, not the default.

## WPF + WebView2 for the overlay UI

Built out fully — three skins hand-ported to WPF, then a WebView2-hosted HTML/CSS version for richer styling — before hitting a structural dead end: WPF's transparency support hit-tests against its own rendered bitmap, which has nothing drawn where a hosted WebView2 child window paints itself. Every click on the overlay landed on whatever was *behind* it on the desktop instead of the overlay's own buttons. Not a bug to fix — it's how separate-window compositing works, and no amount of z-order or event tweaking changes that. Rebuilt in Avalonia instead, which renders its entire window itself with no child-window surface to disagree with, confirmed via real synthetic mouse clicks (not just programmatic invocation) across all three skins post-migration.

## sqlite-vec for memory retrieval

A real, actively-maintained native vector-search extension for SQLite — considered, not used. The memory table is realistically dozens to low hundreds of personal entries, nowhere near the scale where sub-linear vector search matters (retrieval *quality* via cosine similarity is identical whether computed by a linear scan or an index — the index only helps speed at a scale this project isn't at). Separately, it ships as a compiled native loadable extension rather than a pure-managed library — a riskier dependency class this project had already been burned by once (a Windows-automation library that turned out to be .NET-Framework-only, breaking a build after the fact). Brute-force cosine similarity in pure C# gets the same retrieval quality with zero native dependency risk, at a scale where the tradeoff doesn't apply yet. Not a permanent rejection — worth revisiting if the table ever grows large enough for lookup time to become noticeable.

## "3D generation" as originally scoped

An early ask was interpreted as AI-generating 3D models/UI from scratch. Corrected: the actual intent was Nova *driving* existing tools (Figma, Blender) through their own scripting APIs, not generative content — which is exactly why that capability fits cleanly as a self-contained tool built on the existing tool contract rather than needing new "generation" infrastructure.

## The permission model's shape

Started as three tiers modeled loosely on read/write/send risk levels, each with its own confirmation — sensing, then local changes, then external actions. That reads as a bureaucratic form ("can I read → can I edit → can I send") rather than how a competent assistant actually behaves: asked once for a task, shown one review before the consequential step, not three separate checkpoints. Collapsed to two real gates plus free sensing (see [ARCHITECTURE.md](ARCHITECTURE.md#permission-model--one-ask-one-check)) — the "revert never needs approval, only advancing does" rule for self-contained tools fell out of the same logic without needing new design: a prior version already passed review once, so returning to it isn't introducing anything new.

## Ambient watching vs. task chaining

The whole "proactive" behavior was originally designed as one thing: ambient watching plus a cheap model deciding whether to interrupt. It later split into two genuinely different problems: chaining a logical next step onto a task *already asked for* ("compile this" → "want to run the tests?") has no "is this a good moment to interrupt" judgment call to make, because the channel's already open — that turned out to need no new machinery at all, just the agent core reasoning about the obvious next step as part of finishing the current tool call. Noticing something unprompted from an idle state is the actually-different problem, and the only one that needs a gate.

## sherpa-onnx as a faster local speech-to-text engine

Evaluated a streaming Zipformer transducer (via [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)) as a faster, natively-streaming alternative to Whisper. Real, live-tested engineering across padding, feature-extraction config, model context-window size, decoding method, hotword biasing toward domain-specific words, and full-precision vs. quantized weights — each change verified against actual transcription output, not assumed. Two useful negative results came out of it: Whisper showed the same failure pattern sherpa did on identical phrases (ruling out a sherpa-specific bug) and the capture pipeline's own audio showed no corruption at the point of failure (ruling out a pipeline bug). What was left was a genuine accuracy gap that no amount of configuration closed — sherpa's real-world transcription quality on this app's actual microphone and voice stayed behind Whisper's. Whisper remains the default; the sherpa integration stays in the codebase behind a feature flag rather than being ripped out, since the swappable-engine design cost nothing to keep and a future model release could change the answer.
