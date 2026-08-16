using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Small enough that swapping engines (currently Kokoro vs. the experimental
// Chatterbox sidecar - see ChatterboxTtsClient) is a one-line change in
// Program.cs, without NovaAssistant needing to know which one it's using.
internal interface ITtsEngine
{
    // Fires the moment audio playback actually begins - i.e. after
    // synthesis, not when SpeakAsync is first called. NovaAssistant uses
    // this (rather than the call itself) to flip IsSpeaking, so the
    // overlay's talking animations start when Nova is actually audible,
    // not the instant text is handed to the engine.
    event Action? PlaybackStarted;

    // Synthesizes and plays `text`, returning once playback has actually
    // finished - or throwing OperationCanceledException if cancelled
    // (barge-in) at any point.
    Task SpeakAsync(string text, CancellationToken cancellationToken);

    // Immediately halts any current playback. Called directly from the mic
    // capture callback on a sustained barge-in, so must be fast and safe to
    // call concurrently with an in-flight SpeakAsync.
    void StopPlayback();
}
