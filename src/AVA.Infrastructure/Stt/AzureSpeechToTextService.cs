using AVA.Application.Interfaces;
using AVA.Infrastructure.Settings;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVA.Infrastructure.Stt;

// Implements ISpeechToTextService using Azure Cognitive Services.
// Sends raw audio bytes to Azure and receives transcribed text.
//
// KEY DESIGN: Uses PushAudioInputStream (in-memory) instead of a temp file.
// Saves 50-100ms of disk I/O per request - critical for voice latency.
public class AzureSpeechToTextService : ISpeechToTextService
{
    private readonly AzureSettings _settings;
    private readonly ILogger<AzureSpeechToTextService> _logger;

    public AzureSpeechToTextService(
        IOptions<AzureSettings> options,
        ILogger<AzureSpeechToTextService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(byte[] audioBytes, CancellationToken ct = default)
    {
        // Fail fast - validate before calling Azure (saves quota on bad input)
        if (audioBytes is null || audioBytes.Length == 0)
        {
            _logger.LogWarning("TranscribeAsync called with empty audio bytes.");
            return string.Empty;
        }

        _logger.LogInformation("STT: Transcribing {Bytes} bytes", audioBytes.Length);

        var speechConfig = SpeechConfig.FromSubscription(_settings.SpeechKey, _settings.SpeechRegion);
        speechConfig.SpeechRecognitionLanguage = "en-US";

        // Push raw bytes into Azure's in-memory stream.
        // Format must match what the browser sends: 16kHz, 16-bit, mono PCM.
        using var pushStream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        pushStream.Write(audioBytes);
        pushStream.Close();

        using var audioConfig = AudioConfig.FromStreamInput(pushStream);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var result = await recognizer.RecognizeOnceAsync();

        switch (result.Reason)
        {
            case ResultReason.RecognizedSpeech:
                _logger.LogInformation("STT succeeded: \"{Text}\"", result.Text);
                return result.Text;

            case ResultReason.NoMatch:
                _logger.LogWarning("STT: No speech detected in audio.");
                return string.Empty;

            case ResultReason.Canceled:
                var d = CancellationDetails.FromResult(result);
                _logger.LogError("STT cancelled. Reason: {Reason}, Code: {Code}, Details: {Details}",
                    d.Reason, d.ErrorCode, d.ErrorDetails);
                return string.Empty;

            default:
                return string.Empty;
        }
    }
}
