using System;
using System.Collections.Generic;
using SoundFlow.Extensions.WebRtc.Apm;

namespace Nova;

internal static class AudioDsp
{
    // Converts 16-bit PCM bytes from NAudio into normalized [-1, 1] float
    // samples, the format both the VAD's RMS check and Whisper expect.
    // Loopback capture format varies by system (commonly 32-bit IEEE float,
    // sometimes 16-bit PCM) and is usually stereo - downmixes to mono to match
    // what the AEC reference stream is configured for.
    public static float[] LoopbackBytesToMonoFloat(byte[] buffer, int bytesRecorded, int bitsPerSample, int channels)
    {
        int bytesPerSample = bitsPerSample / 8;
        int frameCount = bytesRecorded / (bytesPerSample * channels);
        var mono = new float[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = (i * channels + ch) * bytesPerSample;
                sum += bitsPerSample switch
                {
                    32 => BitConverter.ToSingle(buffer, offset),
                    16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                    _ => 0f,
                };
            }

            mono[i] = sum / channels;
        }

        return mono;
    }

    // WebRTC's Audio Processing Module requires exact 10ms frames per channel,
    // at each stream's own rate - carries partial mic samples across calls
    // (NAudio's callback size doesn't line up with the frame size), and for
    // each complete mic frame, first feeds one reference frame (zero-filled if
    // nothing's queued yet, e.g. before anything has played) so the AEC's
    // internal timing model stays consistent. Returns null if there isn't a
    // full frame's worth of mic audio yet.
    public static float[]? RunEchoCancellation(
        AudioProcessingModule apm,
        float[] rawMicChunk,
        List<float> micFrameCarry,
        int micFrameSize,
        StreamConfig micStreamConfig,
        List<float> referenceBuffer,
        object referenceLock,
        int referenceFrameSize,
        StreamConfig referenceStreamConfig)
    {
        micFrameCarry.AddRange(rawMicChunk);
        if (micFrameCarry.Count < micFrameSize)
        {
            return null;
        }

        var cleaned = new List<float>();
        while (micFrameCarry.Count >= micFrameSize)
        {
            float[] referenceFrame = new float[referenceFrameSize];
            lock (referenceLock)
            {
                int available = Math.Min(referenceFrameSize, referenceBuffer.Count);
                if (available > 0)
                {
                    referenceBuffer.CopyTo(0, referenceFrame, 0, available);
                    referenceBuffer.RemoveRange(0, available);
                }
            }

            float[][] reverseSrc = [referenceFrame];
            float[][] reverseDest = [new float[referenceFrameSize]];
            apm.ProcessReverseStream(reverseSrc, referenceStreamConfig, referenceStreamConfig, reverseDest);

            float[] micFrame = micFrameCarry.GetRange(0, micFrameSize).ToArray();
            micFrameCarry.RemoveRange(0, micFrameSize);

            float[][] micSrc = [micFrame];
            float[][] micDest = [new float[micFrameSize]];
            apm.ProcessStream(micSrc, micStreamConfig, micStreamConfig, micDest);

            cleaned.AddRange(micDest[0]);
        }

        return cleaned.ToArray();
    }

    public static float[] BytesToFloatSamples(byte[] buffer, int bytesRecorded)
    {
        var samples = new float[bytesRecorded / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }

        return samples;
    }

    public static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double sumSquares = 0;
        foreach (float s in samples)
        {
            sumSquares += s * s;
        }

        return (float)Math.Sqrt(sumSquares / samples.Length);
    }
}
