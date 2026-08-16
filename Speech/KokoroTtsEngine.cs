using System;
using System.Threading;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;

namespace Nova;

// Local, near-instant CPU synthesis - the original/default engine. See
// ChatterboxTtsClient for the experimental (GPU + local network) alternative
// being evaluated for more natural-sounding speech.
internal sealed class KokoroTtsEngine : ITtsEngine
{
    private readonly KokoroTTS _tts;
    private readonly KokoroVoice _voice;

    // Calling SpeakFast again before the previous call finishes playing seems
    // to interrupt it rather than queue it (its own usage example only ever
    // fires one call at a time) - completion is signaled via this TCS, set by
    // OnSpeechCompleted/OnSpeechCanceled, awaited before returning.
    private TaskCompletionSource? _currentSpeechTcs;

    public event Action? PlaybackStarted;

    public KokoroTtsEngine(KokoroTTS tts, KokoroVoice voice)
    {
        _tts = tts;
        _voice = voice;
        _tts.OnSpeechStarted += _ => PlaybackStarted?.Invoke();
        _tts.OnSpeechCompleted += _ => _currentSpeechTcs?.TrySetResult();
        _tts.OnSpeechCanceled += _ => _currentSpeechTcs?.TrySetResult();
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        _currentSpeechTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tts.SpeakFast(text, _voice);

        // Generous timeout so a missed completion event can never hang the
        // app forever; Task.Delay(Infinite, cancellationToken) is what lets a
        // barge-in (which stops playback via StopPlayback, called separately)
        // unblock this wait.
        await Task.WhenAny(
            _currentSpeechTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None),
            Task.Delay(Timeout.Infinite, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void StopPlayback() => _tts.StopPlayback();
}
