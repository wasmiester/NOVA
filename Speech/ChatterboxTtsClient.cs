using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Nova;

// Experimental: Chatterbox (Resemble AI), being evaluated as a possible
// replacement for Kokoro if it sounds more natural. Chatterbox has no .NET
// bindings, so a small local Python/Flask sidecar (tts-sidecar/server.py)
// does the actual generation on the GPU; this class just talks to it over
// localhost and plays the returned WAV through NAudio. Program.cs owns
// launching/killing the sidecar process.
//
// Real tradeoff versus Kokoro: generation is now a network + GPU-inference
// round trip per sentence instead of near-instant local synthesis, so every
// sentence has real latency before playback starts - worth watching for
// while evaluating whether this is actually worth keeping.
internal sealed class ChatterboxTtsClient : ITtsEngine, IDisposable
{
    private readonly HttpClient _http;
    private readonly WaveOutEvent _waveOut = new();
    private TaskCompletionSource? _playbackTcs;

    public event Action? PlaybackStarted;

    public ChatterboxTtsClient(Uri baseAddress)
    {
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        _waveOut.PlaybackStopped += (_, _) => _playbackTcs?.TrySetResult();
    }

    // Polls the sidecar until it responds - the model load (plus, on first
    // run, downloading several GB of weights) can take a while.
    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync("health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Sidecar not listening yet - keep polling.
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { text });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync("speak", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] wavBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        using var wavStream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(wavStream);

        _playbackTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveOut.Init(reader);
        _waveOut.Play();
        PlaybackStarted?.Invoke();

        // Generous timeout so a missed PlaybackStopped event can never hang
        // the app forever; Task.Delay(Infinite, cancellationToken) is what
        // lets a barge-in (which stops playback via StopPlayback, called
        // separately) unblock this wait.
        await Task.WhenAny(
            _playbackTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None),
            Task.Delay(Timeout.Infinite, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
    }

    // Called directly from the mic capture callback on a sustained barge-in -
    // must be fast and safe to call concurrently with an in-flight SpeakAsync.
    public void StopPlayback() => _waveOut.Stop();

    public void Dispose()
    {
        _waveOut.Dispose();
        _http.Dispose();
    }
}
