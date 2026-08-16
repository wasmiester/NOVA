using System.Text;
using System.Threading.Tasks;
using Whisper.net;

namespace Nova;

// Wraps the existing Whisper.net pipeline behind ISttEngine - pulled out of
// NovaAssistant.TranscribeAsync unchanged, just relocated so it's swappable
// with SherpaOnnxSttEngine (see Program.cs's UseSherpaOnnxStt flag).
internal sealed class WhisperSttEngine(WhisperProcessor processor) : ISttEngine
{
    public async Task<string?> TranscribeAsync(float[] samples)
    {
        var transcript = new StringBuilder();
        await foreach (SegmentData segment in processor.ProcessAsync(samples))
        {
            transcript.Append(segment.Text);
        }

        string text = transcript.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
