using System.Threading.Tasks;

namespace Nova;

// Small enough that swapping engines (currently Whisper vs. the
// experimental sherpa-onnx streaming path) is a one-line change in
// Program.cs, without NovaAssistant needing to know which one it's using -
// same pattern as ITtsEngine.
internal interface ISttEngine
{
    // Transcribes a finished utterance buffer (already VAD-boundary-detected
    // by AudioCapturePipeline) and returns the recognized text, or null if
    // nothing intelligible came through. Given the whole buffer at once, not
    // fed incrementally - AudioCapturePipeline's RMS/VAD/barge-in system
    // still owns "when does an utterance start/end," this only owns "what
    // did they say."
    Task<string?> TranscribeAsync(float[] samples);
}
