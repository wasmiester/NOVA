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
    public SherpaOnnxSttEngine(string modelDir)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRateHz;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner-epoch-99-avg-1-chunk-16-left-64.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
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
    // AudioCapturePipeline's own speech-RMS thresholds (~0.0065-0.0077),
    // so it's silence in every practical sense, just not exactly zero.
    private const float PaddingNoiseAmplitude = 0.0005f;

    public Task<string?> TranscribeAsync(float[] samples)
    {
        using OnlineStream stream = _recognizer.CreateStream();

        float[] leadPadding = GenerateLowLevelNoise((int)(SampleRateHz * 0.3));
        float[] tailPadding = GenerateLowLevelNoise((int)(SampleRateHz * 0.6));
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
