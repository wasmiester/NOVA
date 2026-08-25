using System;
using System.IO;
using System.Threading.Tasks;
using SherpaOnnx;

namespace Nova;

// Experimental alternative to Whisper - a streaming Zipformer transducer via
// sherpa-onnx (see docs/DESIGN_DECISIONS.md), chosen over Moonshine
// specifically because sherpa-onnx's own C# Moonshine examples are
// non-streaming; this is the library's actual streaming-native path, and
// Zipformer transducer decode is typically much faster than Whisper's
// cross-attention decode at a comparable size.
//
// Still only swaps *what* transcribes a finished utterance buffer, not
// *when* one starts/ends - TranscribeAsync receives one complete buffer at
// a time, same contract as WhisperSttEngine (see ISttEngine's doc comment).
// AudioCapturePipeline's already-tuned RMS/AEC/barge-in system decides
// utterance boundaries either way; true incremental (token-by-token,
// while-you're-still-talking) streaming would mean rebuilding that system
// around this engine's own IsEndpoint-based segmentation instead - a much
// bigger, riskier change than swapping the transcription engine alone, and
// not attempted here.
internal sealed class SherpaOnnxSttEngine : ISttEngine
{
    private const int SampleRateHz = 16000;

    private readonly OnlineRecognizer _recognizer;

    // FeatConfig.SampleRate/FeatureDim were both missing entirely in the
    // first version of this constructor - only AcceptWaveform's own
    // sampleRate argument was set, never the config's. If the binding's
    // internal default doesn't actually match 16kHz/80 (its silence on
    // this is exactly the kind of thing that varies by binding version),
    // every feature frame gets computed wrong, which fits "real phonetic
    // content leaks through but heavily scrambled" far better than the
    // padding issue alone did - confirmed still garbled even after that
    // fix. Set explicitly now, matching sherpa-onnx's own
    // online-decode-files example. ModelType (set speculatively in the
    // first version, "zipformer2") is gone too - that example never sets
    // it for a transducer model at all, so it was an unverified guess, not
    // something actually confirmed necessary or even correct.
    // "left-64" (64 frames of left/past context) traded accuracy for lower
    // latency - confirmed still producing consistently near-miss-but-wrong
    // phonetic content ("HE NOVELS AGOING" for "Hey Nova('s) going") even
    // after the padding and FeatConfig fixes above, which is exactly what
    // too little context looks like: real signal, systematically
    // under-informed decoding. "left-128" trades some of that latency back
    // for roughly double the past context the model can actually use.
    public SherpaOnnxSttEngine(string modelDir)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRateHz;
        config.FeatConfig.FeatureDim = 80;
        // Full fp32 weights, not .int8 - see Program.cs's SherpaOnnxModelFiles
        // doc comment for why (quantization-driven accuracy loss, untested
        // until now, was still on the table even after ruling out padding,
        // context, decoding method, and raw capture-pipeline corruption).
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder-epoch-99-avg-1-chunk-16-left-128.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner-epoch-99-avg-1-chunk-16-left-128.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;

        // "Hey Nova" itself, not padding or context, has been the one part
        // consistently mis-transcribed across every round of live testing so
        // far ("HANOVA", "HAINOVA", "THEY KNOW...") - including after fixing
        // both the padding-seam issue and the pre-roll warm-up context (see
        // LeadPaddingSeconds below and Audio/AudioCapturePipeline.cs's
        // PreRollChunks). Ordinary English right after it consistently
        // transcribes correctly, which is the actual signature of an
        // out-of-vocabulary word, not a warm-up problem - "Nova" is a proper
        // noun this general-English-trained model's data almost certainly
        // under-represents. Hotwords are sherpa-onnx's real, documented
        // mechanism for biasing decoding toward specific words (verified
        // against its own docs and this exact installed binding's fields via
        // reflection, not guessed) - only supported under
        // modified_beam_search, not the greedy_search default, hence that
        // change too. Known tradeoff, confirmed via sherpa-onnx's own issue
        // tracker: modified_beam_search can occasionally emit spurious short
        // words during silence/pauses where greedy_search stays quiet - if
        // that shows up, it's this decoding-method change, not the hotword
        // itself.
        // BpeVocab does NOT take the raw sentencepiece bpe.model binary -
        // confirmed live, it crashed the whole app at startup ("Each line in
        // vocab should contain two items... the first one is bpe token, the
        // second one is score"). It wants a plain-text "token score" dump,
        // a different artifact than the .model file this HF repo ships.
        // Not wired up yet - see hotwords.txt below, which still ends up
        // skipped without this, but that fails soft (a console warning,
        // decoding otherwise proceeds normally) rather than crashing.
        string hotwordsPath = Path.Combine(modelDir, "hotwords.txt");
        File.WriteAllText(hotwordsPath, "NOVA\n");
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;
        config.HotwordsFile = hotwordsPath;
        config.HotwordsScore = 2.0f;

        _recognizer = new OnlineRecognizer(config);
    }

    // Adding lead/tail padding (below) fixed the crash and got real words
    // showing up correctly mid-transcription, but confirmed live across
    // three separate utterances: every single result still had a garbled
    // *prefix* specifically - "AND OVER WHAT'S TOO PLUS DO" for "what's
    // two plus two," "YOU KNOW HOW TO GOING" for "how's it going" - real
    // recognizable words later in the string, junk at the start every
    // time. That consistent pattern (not random per-utterance garbage)
    // points at the padding itself: `new float[n]` is mathematically
    // perfect zero-value silence, an input this model never saw in
    // training (real recordings always have some room tone/noise floor),
    // and streaming ASR models are a known case of hallucinating phantom
    // words specifically when fed dead silence like that. Low-amplitude
    // random noise instead - a standard "avoid feeding a model an
    // unnaturally perfect signal" trick - well below
    // AudioCapturePipeline's own speech-RMS thresholds (~0.0077-0.0081),
    // so it's silence in every practical sense, just not exactly zero.
    private const float PaddingNoiseAmplitude = 0.0005f;

    // Confirmed live, twice now: real words come through correctly for
    // most of every utterance, but the opening word or two consistently
    // doesn't ("Hey Nova" -> "HANOVA"/"THEY KNOW ABOUT", right before
    // "HOW ARE YOU DOIN"/"CHECK MY" landed perfectly). That's the
    // signature of a left-context window that hasn't finished warming up
    // yet when real speech starts - "left-128" needs 128 frames of past
    // context, and at this model's ~10ms/frame rate that's ~1.28s.
    // First attempt at fixing this just grew this constant (0.3s -> 2s) -
    // wrong lever. This is *synthetic* noise glued directly onto the real
    // captured buffer; growing it only moves the noise-to-real seam
    // earlier, it doesn't remove the seam, and that seam sits right where
    // real speech starts either way, likely disrupting the model's
    // streaming state exactly when it matters regardless of how much
    // padding precedes it. Real fix is upstream:
    // Audio/AudioCapturePipeline.cs's pre-roll buffer now keeps ~1.3s of
    // genuine captured audio (continuous with the real speech that
    // follows it, this mic's own actual noise floor) instead of leaving
    // sherpa to warm up on fabricated noise. This constant now only needs
    // to cover the gap for the rare case the pre-roll queue hasn't
    // finished filling yet (e.g. the very first utterance right after
    // startup) - kept small deliberately so most of the warm-up context
    // is real audio, not synthetic.
    private const double LeadPaddingSeconds = 0.3;
    private const double TailPaddingSeconds = 0.6;

    public Task<string?> TranscribeAsync(float[] samples)
    {
        using OnlineStream stream = _recognizer.CreateStream();

        float[] leadPadding = GenerateLowLevelNoise((int)(SampleRateHz * LeadPaddingSeconds));
        float[] tailPadding = GenerateLowLevelNoise((int)(SampleRateHz * TailPaddingSeconds));
        stream.AcceptWaveform(SampleRateHz, leadPadding);
        stream.AcceptWaveform(SampleRateHz, samples);
        stream.AcceptWaveform(SampleRateHz, tailPadding);
        stream.InputFinished();

        while (_recognizer.IsReady(stream))
        {
            _recognizer.Decode(stream);
        }

        string text = _recognizer.GetResult(stream).Text.Trim();
        return Task.FromResult(string.IsNullOrWhiteSpace(text) ? null : text);
    }

    private static float[] GenerateLowLevelNoise(int length)
    {
        var noise = new float[length];
        for (int i = 0; i < length; i++)
        {
            noise[i] = (float)(Random.Shared.NextDouble() * 2 - 1) * PaddingNoiseAmplitude;
        }

        return noise;
    }
}
