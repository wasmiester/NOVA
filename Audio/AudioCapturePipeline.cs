using System;
using System.Collections.Generic;
using NAudio.Wave;
using SoundFlow.Extensions.WebRtc.Apm;
using WasapiLoopbackCapture = NAudio.Wave.WasapiLoopbackCapture;

namespace Nova;

// Real acoustic echo cancellation (the actual technique behind Zoom/Teams
// muting themselves out of your mic), not another threshold tweak. The
// reference (what's hitting the speakers) comes from WASAPI loopback
// capture rather than hooking into KokoroSharp specifically - it's
// TTS-agnostic and captures whatever the system is actually playing.
// WebRTC's Audio Processing Module needs exact 10ms frames per channel, at
// each stream's own rate, so both sides carry a rolling buffer and only
// hand off complete frames.
//
// Owns the mic/loopback capture and voice-activity gating; NovaAssistant
// owns everything downstream of "an utterance is ready to process." The two
// meet at a small surface: IsBusy/IsSpeaking (read), and StopSpeaking() /
// DispatchUtterance() / DispatchInterjection() (called from the mic
// callback on a spoken barge-in, a fresh utterance, or "dynamic talking" -
// speech captured while she's busy but not speaking, e.g. mid-tool-call).
// A barge-in only silences her voice (StopSpeaking) - it no longer hard-
// cancels the task by itself; the interrupting utterance is captured as an
// interjection like any other and NovaAssistant decides afterward, once the
// words are known, whether it actually meant stop (see
// StopIntentClassifier/Interrupt()).
internal sealed class AudioCapturePipeline : IDisposable
{
    private const int SampleRateHz = 16000;

    // VAD tuning - depends heavily on mic sensitivity, room noise, and personal
    // speech pace. SilenceEndMs in particular is a "how long do you tend to
    // pause mid-sentence" knob, not a fixed constant - raise it further if she
    // still cuts in before you're done.
    private const float SpeechRmsThreshold = 0.0077f; // another ~20% more sensitive (0.0096 -> 0.0077) -
                                                        // still needing to speak assertively for normal pickup
    // Separate, higher threshold specifically for cutting Nova off mid-reply.
    // AEC (added later) cancels most of her own voice bleeding back through the
    // mic, but real AEC is never perfect - some residual can still cross this,
    // so barge-in needs a louder, more deliberate sound to fire in the first
    // place, and (see BargeInSustainMs below) needs to actually last a moment.
    // Restored to 0.0081 (was dropped to 0.0065 in an earlier general
    // sensitivity pass, in lockstep with SpeechRmsThreshold above rather
    // than for its own reason) - confirmed live: Nova was barging in on her
    // own residual echo, worse with system output volume turned up, since
    // WASAPI loopback captures the actual post-volume-scaled signal and a
    // louder signal leaves proportionally more residual after AEC's linear
    // cancellation (real speakers distort more at higher volume, which a
    // linear adaptive filter can't fully model). A threshold *below*
    // SpeechRmsThreshold meant residual echo had an easier bar to clear
    // here than genuine distant speech has for normal pickup - backwards
    // from this constant's own stated purpose above. Restoring it above
    // SpeechRmsThreshold, not just relying on BargeInSustainMs, targets the
    // actual dimension the volume-dependence points at (amplitude), where
    // sustain duration alone only helps if the residual has natural gaps -
    // untrue for a continuous leak of Nova's own continuous speech.
    private const float BargeInRmsThreshold = 0.0081f;
    // Genuine interruptions are sustained speech; residual echo that leaks past
    // imperfect AEC tends to be brief, syllable-length spikes. Requiring the
    // elevated volume to hold for this long before actually cutting her off
    // filters those out without needing AEC to be perfect - this is the more
    // load-bearing fix of the two, since it doesn't depend on tuning the AEC
    // delay estimate exactly right.
    // Raised from 90ms - confirmed live: Nova was barging in on her own
    // voice (residual self-echo leaking past AEC, see BargeInRmsThreshold
    // above for the amplitude side of this same incident), cutting herself
    // off mid-reply and producing disconnected transcribed fragments that
    // read like they'd come from somewhere else entirely. A longer required
    // hold is a second, independent lever on the same problem - it only
    // helps to the extent the residual leak has real gaps in it (e.g.
    // between her words/phrases) rather than being one continuous elevated
    // stretch for as long as she's talking; the amplitude fix above is the
    // more direct match for what actually seems to be happening (worse at
    // higher system output volume), so treat this as a complementary
    // safety margin, not the primary fix.
    private const int BargeInSustainMs = 220;
    // A sharp, brief transient (a keyboard click, a knock) can cross
    // SpeechRmsThreshold just as easily as real speech, but a fresh
    // utterance start had no sustain check at all - unlike the barge-in
    // path above, which already required this. Confirmed live: mechanical
    // typing noise was triggering full utterance capture, sent to Whisper
    // as garbage a couple seconds later. Genuine speech sustains well past
    // one 10-20ms chunk even for a short word; a click doesn't.
    private const int FreshUtteranceSustainMs = 80;
    private const int SilenceEndMs = 2800; // raised from 2000 - was cutting in before a normal mid-sentence pause finished
    private const int MinUtteranceMs = 400;
    // ~300ms was tuned against Whisper, a batch model that tolerates a
    // slightly-clipped word start fine. The streaming sherpa-onnx engine
    // (see Speech/SherpaOnnxSttEngine.cs) needs ~1.28s of real left-context
    // before its streaming state is actually warmed up - short of that, it
    // consistently mis-transcribes the first word or two of every utterance
    // no matter how much *synthetic* padding gets glued on downstream,
    // because the synthetic-to-real seam sits right at the real speech
    // onset either way. Bumped to give sherpa real captured audio (this
    // mic's own noise floor, continuous with what follows) to warm up on
    // instead, rather than fabricated noise with a hard seam right where
    // speech starts. Harmless for Whisper too - extra real lead-in silence
    // doesn't hurt a batch model.
    private const int PreRollChunks = 13; // ~1.3s of audio kept from before speech is detected

    // Both sherpa-onnx AND Whisper garble the same "Hey Nova ..." prefix on
    // otherwise-identical utterances (confirmed live, same test phrases,
    // same "and over ..." nonsense prefix from both engines) - proves the
    // problem isn't either STT engine, it's something in this shared
    // capture path producing a genuinely corrupted first ~100-300ms rather
    // than two unrelated models coincidentally guessing wrong the same way.
    // Prints a coarse RMS-over-time profile of every utterance's first
    // ~500ms so an actual amplitude anomaly (a dropout, a spike, a click)
    // can be seen directly instead of inferred secondhand from what two
    // different models guess garbled audio sounds like. Diagnosed live:
    // profile was clean (a normal, gradual speech attack, no click/dropout/
    // clipping) - rules out capture-pipeline corruption as the cause of the
    // "Hey Nova" garbling. Off now that its question is answered; flip back
    // on if a future capture-side issue needs the same kind of real
    // amplitude evidence instead of another guess.
    private const bool DebugLogOnsetProfile = false;

    private readonly NovaAssistant _assistant;

    private readonly AudioProcessingModule _apm = new();
    private readonly ApmConfig _apmConfig = new();
    private readonly WasapiLoopbackCapture _loopback = new();
    private readonly WaveInEvent _waveIn;

    private readonly int _referenceChannels;
    private readonly int _referenceFrameSize;
    private readonly StreamConfig _referenceStreamConfig;
    private readonly StreamConfig _micStreamConfig = new(SampleRateHz, 1);
    private readonly int _micFrameSize;

    private readonly List<float> _referenceBuffer = [];
    private readonly object _referenceLock = new();
    private readonly List<float> _micFrameCarry = [];

    private readonly List<float> _utteranceBuffer = [];
    private readonly Queue<float[]> _preRollBuffer = [];
    // Chunks captured while a barge-in is being sustain-confirmed - this is
    // the user's genuine interrupting speech, and seeds the new utterance
    // if the barge-in confirms (see OnMicDataAvailable). Without this, that
    // audio was simply dropped during the confirmation window, and the new
    // utterance was seeded from _preRollBuffer instead - which holds Nova's
    // own trailing audio from just before the interrupt, not the user's,
    // corrupting the start of the post-interrupt transcription.
    private readonly List<float[]> _bargeInCandidateChunks = [];
    // Same idea as _bargeInCandidateChunks above, for a fresh utterance's
    // own sustain-confirmation window (see FreshUtteranceSustainMs) - real
    // captured audio from during that window, not discarded once confirmed.
    private readonly List<float[]> _freshUtteranceCandidateChunks = [];
    private bool _userIsSpeaking;
    // True when the utterance currently being captured started while Nova
    // was busy but not speaking (a silent tool-execution stretch) - decides
    // whether the finished clip is dispatched as a fresh task or as a
    // "dynamic talking" interjection for the task already running. Distinct
    // from the barge-in path above (which fires while she's actively
    // speaking and always starts fresh) - see NovaAssistant.DispatchInterjection.
    private bool _capturingInterjection;
    private DateTime _lastVoiceAt = DateTime.MinValue;
    private DateTime? _bargeInCandidateSince;
    private DateTime? _freshUtteranceCandidateSince;

    public AudioCapturePipeline(NovaAssistant assistant)
    {
        _assistant = assistant;

        _apmConfig.SetEchoCanceller(enabled: true, mobileMode: false);
        // Voice isolation - suppresses steady background noise (fans, hum,
        // hiss) before it ever reaches the RMS/VAD thresholds above. Not
        // full multi-speaker separation (isolating one voice from another
        // person talking at the same time needs a real source-separation
        // model), just WebRTC's own noise suppressor, already bundled in
        // the same library the echo canceller above uses.
        _apmConfig.SetNoiseSuppression(enabled: true, level: NoiseSuppressionLevel.High);
        _apm.Initialize();
        _apm.ApplyConfig(_apmConfig);
        // WasapiLoopbackCapture doesn't expose configurable buffer duration (fixed
        // internally, typically ~100ms) - 50ms was too low an estimate against that.
        // Reduced further below via WaveInEvent.BufferMilliseconds on the mic side,
        // the one buffer actually under our control, so the combined estimate here
        // is more plausible - still an estimate, not a measurement, and the
        // sustained-duration check below is the more load-bearing fix since it
        // doesn't depend on getting this exactly right.
        _apm.SetStreamDelayMs(120);

        _referenceChannels = _loopback.WaveFormat.Channels;
        _referenceFrameSize = AudioProcessingModule.GetFrameSize(_loopback.WaveFormat.SampleRate);
        _referenceStreamConfig = new StreamConfig(_loopback.WaveFormat.SampleRate, 1); // downmixed to mono below
        _micFrameSize = AudioProcessingModule.GetFrameSize(SampleRateHz);

        _loopback.DataAvailable += OnLoopbackDataAvailable;

        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRateHz, 16, 1), BufferMilliseconds = 20 };
        _waveIn.DataAvailable += OnMicDataAvailable;
    }

    public void Start()
    {
        _loopback.StartRecording();
        _waveIn.StartRecording();
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        float[] mono = AudioDsp.LoopbackBytesToMonoFloat(e.Buffer, e.BytesRecorded, _loopback.WaveFormat.BitsPerSample, _referenceChannels);
        lock (_referenceLock)
        {
            _referenceBuffer.AddRange(mono);
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        float[]? chunk = AudioDsp.RunEchoCancellation(
            _apm, AudioDsp.BytesToFloatSamples(e.Buffer, e.BytesRecorded), _micFrameCarry, _micFrameSize, _micStreamConfig,
            _referenceBuffer, _referenceLock, _referenceFrameSize, _referenceStreamConfig);
        if (chunk is null)
        {
            return; // not enough samples yet for a full 10ms frame
        }

        float rms = AudioDsp.ComputeRms(chunk);

        bool novaCurrentlySpeaking = _assistant.IsSpeaking;
        float activeThreshold = novaCurrentlySpeaking ? BargeInRmsThreshold : SpeechRmsThreshold;

        if (rms >= activeThreshold)
        {
            if (novaCurrentlySpeaking)
            {
                // Require the elevated volume to actually sustain before treating
                // it as a real interruption - a brief spike (residual echo that
                // slipped past imperfect AEC) resets the clock instead of firing.
                // Every chunk seen during this window is genuine candidate
                // speech - buffered so it isn't just discarded if this does
                // turn out to be a real interruption (see below).
                _bargeInCandidateSince ??= DateTime.UtcNow;
                _bargeInCandidateChunks.Add(chunk);
                if ((DateTime.UtcNow - _bargeInCandidateSince.Value).TotalMilliseconds < BargeInSustainMs)
                {
                    return;
                }

                // Sustained long enough - silence her and hand the mic to the
                // interrupting speech, seeded with the candidate audio just
                // captured (the user's actual interrupting speech), not the
                // pre-roll buffer - that holds Nova's own trailing audio from
                // just before the interrupt, not the user's, and using it
                // here corrupted the start of the post-interrupt
                // transcription. Captured as an interjection, not a fresh
                // utterance (_capturingInterjection = true) - talking over
                // her doesn't mean stop the task, just stop talking; see
                // StopSpeaking's doc comment.
                _bargeInCandidateSince = null;
                _assistant.StopSpeaking();
                _capturingInterjection = true;
                _preRollBuffer.Clear();
                foreach (float[] candidateChunk in _bargeInCandidateChunks)
                {
                    _utteranceBuffer.AddRange(candidateChunk);
                }

                _bargeInCandidateChunks.Clear();
                _userIsSpeaking = true;
                _lastVoiceAt = DateTime.UtcNow;
                return;
            }

            // Nova being busy no longer drops this outright ("dynamic
            // talking") - it just changes what a finished utterance becomes
            // below: a fresh task if she's genuinely idle, or an
            // interjection handed to whatever task is already running if
            // she's mid-tool-execution. A spoken barge-in (above) is still
            // the only path that hard-interrupts her.
            if (!_userIsSpeaking)
            {
                // Same sustain-confirmation shape as the barge-in branch
                // above (FreshUtteranceSustainMs) - a fresh utterance never
                // had this check before, so a single sharp transient (a
                // keyboard click) committed to _userIsSpeaking immediately.
                // Every chunk seen during the window is real candidate
                // speech, buffered so a genuine word's own onset isn't lost
                // once confirmed.
                _freshUtteranceCandidateSince ??= DateTime.UtcNow;
                _freshUtteranceCandidateChunks.Add(chunk);
                if ((DateTime.UtcNow - _freshUtteranceCandidateSince.Value).TotalMilliseconds < FreshUtteranceSustainMs)
                {
                    return;
                }

                _freshUtteranceCandidateSince = null;
                _capturingInterjection = _assistant.IsBusy;
                // Confirmed live: a fresh utterance landing right after
                // Nova finishes one sentence of a multi-sentence reply
                // (a real gap - IsSpeaking goes false between sentences,
                // see RunAssistantTurnAsync's per-sentence-boundary
                // SpeakAndWaitAsync awaits) got classified as a fresh
                // utterance and stole/cancelled the still-in-flight task
                // instead of continuing gracefully as an interjection -
                // meaning IsBusy read false at this exact moment despite
                // the round loop apparently still being mid-response,
                // which contradicts how _isBusy is supposed to stay
                // claimed for a task's entire duration. Logged here
                // instead of guessed at again - the next occurrence should
                // make the actual mechanism visible instead of needing
                // another round of static analysis.
                StatusLog.WriteLine($"[fresh-utterance classification: IsBusy={_capturingInterjection}, IsSpeaking={_assistant.IsSpeaking}]");

                // Pre-roll only makes sense seeding a *fresh* utterance
                // while genuinely idle - while she's mid-task, recent quiet
                // audio isn't useful lead-in for an interjection the same way.
                if (!_capturingInterjection)
                {
                    foreach (float[] preRollChunk in _preRollBuffer)
                    {
                        _utteranceBuffer.AddRange(preRollChunk);
                    }
                }

                _preRollBuffer.Clear();
                foreach (float[] candidateChunk in _freshUtteranceCandidateChunks)
                {
                    _utteranceBuffer.AddRange(candidateChunk);
                }

                // candidateChunk above already includes this call's own
                // chunk (added to the candidate list before the sustain
                // check ran) - returning here instead of falling through to
                // the shared tail below avoids adding it a second time.
                _freshUtteranceCandidateChunks.Clear();
                _userIsSpeaking = true;
                _lastVoiceAt = DateTime.UtcNow;
                return;
            }

            _userIsSpeaking = true;
            _lastVoiceAt = DateTime.UtcNow;
            _utteranceBuffer.AddRange(chunk);
            return;
        }

        _bargeInCandidateSince = null; // volume dropped - any sustain streak resets
        _bargeInCandidateChunks.Clear();
        _freshUtteranceCandidateSince = null;
        _freshUtteranceCandidateChunks.Clear();

        if (!_userIsSpeaking)
        {
            // Confirmed live: gating this on !IsBusy meant the buffer got
            // zero new audio for the entire duration of a task, including
            // while Nova was speaking her final reply - so the exact
            // utterance most likely to follow right on her heels (the user
            // responding as soon as she stops, the normal case) was also
            // the one most starved of the real lead-in PreRollChunks exists
            // to provide, reproducing the "first word or two garbled"
            // problem that constant was raised to fix in the first place.
            // Safe to accumulate regardless of IsBusy: this branch is only
            // reached when rms is already below the active threshold, so
            // whatever's here (genuine silence, or residual after AEC
            // cancels her own voice) already reads as "quiet" to the same
            // gate real silence does - nothing louder ever lands here. Only
            // *using* pre-roll still stays scoped to a genuinely fresh,
            // non-interjection utterance (see _capturingInterjection below);
            // this only changes when it's collected, not when it's spent.
            _preRollBuffer.Enqueue(chunk);
            while (_preRollBuffer.Count > PreRollChunks)
            {
                _preRollBuffer.Dequeue();
            }

            return;
        }

        _utteranceBuffer.AddRange(chunk);
        if ((DateTime.UtcNow - _lastVoiceAt).TotalMilliseconds < SilenceEndMs)
        {
            return;
        }

        _userIsSpeaking = false;
        float[] finished = _utteranceBuffer.ToArray();
        _utteranceBuffer.Clear();

        if (DebugLogOnsetProfile)
        {
            LogOnsetProfile(finished);
        }

        if (finished.Length < SampleRateHz * MinUtteranceMs / 1000)
        {
            return;
        }

        if (_capturingInterjection)
        {
            _assistant.DispatchInterjection(finished);
        }
        else
        {
            _assistant.DispatchUtterance(finished);
        }
    }

    private static void LogOnsetProfile(float[] samples)
    {
        const int windowMs = 50;
        int windowSize = SampleRateHz * windowMs / 1000;
        int windows = Math.Min(10, samples.Length / windowSize); // first ~500ms
        var parts = new List<string>();
        for (int w = 0; w < windows; w++)
        {
            float[] window = samples[(w * windowSize)..((w + 1) * windowSize)];
            float rms = AudioDsp.ComputeRms(window);
            float peak = 0f;
            foreach (float s in window)
            {
                peak = Math.Max(peak, Math.Abs(s));
            }

            parts.Add($"[{w * windowMs}ms rms={rms:F4} peak={peak:F4}]");
        }

        Console.WriteLine($"[onset profile, {samples.Length} samples total] {string.Join(" ", parts)}");
    }

    public void Dispose()
    {
        _waveIn.DataAvailable -= OnMicDataAvailable;
        _loopback.DataAvailable -= OnLoopbackDataAvailable;
        _waveIn.Dispose();
        _loopback.Dispose();
        _apmConfig.Dispose();
        _apm.Dispose();
    }
}
