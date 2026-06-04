using AVA.Application.Interfaces;
using AVA.Infrastructure.Settings;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace AVA.Infrastructure.Tts;

// Implements ITextToSpeechService using Azure Neural TTS.
// Streams audio in 4KB chunks so the browser starts playing immediately.
//
// Voice: en-US-JennyNeural - warm, empathetic, natural sounding.
// Format: 16kHz 32kbps MP3 - small file size, supported in all browsers.
public class AzureTextToSpeechService : ITextToSpeechService
{
    private readonly AzureSettings _settings;
    private readonly ILogger<AzureTextToSpeechService> _logger;
    private const int ChunkSizeBytes = 4096; // 4KB per chunk

    public AzureTextToSpeechService(
        IOptions<AzureSettings> options,
        ILogger<AzureTextToSpeechService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text,
        string voice = "en-US-JennyNeural",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("SynthesizeStreamAsync called with empty text.");
            yield break; // yield break = exit an IAsyncEnumerable early
        }

        _logger.LogInformation("TTS: Synthesizing {Chars} chars with voice {Voice}", text.Length, voice);

        var speechConfig = SpeechConfig.FromSubscription(_settings.SpeechKey, _settings.SpeechRegion);
        speechConfig.SpeechSynthesisVoiceName = voice;

        // Audio format: 16kHz, 16-bit, Mono WAV (PCM)
        // WAV chosen because all browsers play it natively from a Blob URL — no codec needed.
        // MP3 has browser Blob playback issues on HTTP; WAV has zero compatibility problems.
        speechConfig.SetSpeechSynthesisOutputFormat(
            SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);

        // null AudioConfig = output to memory (not to speaker or file)
        using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);
        var result = await synthesizer.SpeakTextAsync(text);

        if (result.Reason == ResultReason.Canceled)
        {
            var d = SpeechSynthesisCancellationDetails.FromResult(result);
            _logger.LogError("TTS cancelled. Code: {Code}, Details: {Details}",
                d.ErrorCode, d.ErrorDetails);
            yield break;
        }

        var audio = result.AudioData;
        _logger.LogInformation("TTS: Produced {Bytes} bytes, streaming in {Chunks} chunks",
            audio.Length, (int)Math.Ceiling(audio.Length / (double)ChunkSizeBytes));

        // Stream in 4KB chunks - browser can start playing first chunk
        // while the rest are still being sent.
        for (int i = 0; i < audio.Length; i += ChunkSizeBytes)
        {
            ct.ThrowIfCancellationRequested();
            yield return audio[i..Math.Min(i + ChunkSizeBytes, audio.Length)];
        }
    }
}
