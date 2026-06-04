namespace AVA.Application.Interfaces;

// WHY AN INTERFACE?
// Business logic never calls Azure directly - it calls this interface.
// In unit tests we inject a fake. In production we inject AzureSpeechToTextService.
// To switch to Whisper: change 1 line in Program.cs. Nothing else changes.
// This is the Dependency Inversion Principle - the D in SOLID.
//
// Implementations: AzureSpeechToTextService, WhisperSttService
public interface ISpeechToTextService
{
    // audioBytes: Raw PCM/WAV audio from the microphone
    // ct: Cancel if the user hangs up mid-transcription
    // Returns: Transcribed text, or empty string if nothing detected
    Task<string> TranscribeAsync(byte[] audioBytes, CancellationToken ct = default);
}
