namespace AVA.Application.Interfaces;

// WHY IAsyncEnumerable (streaming)?
// Without streaming: wait for ALL audio to generate = 2-3s of silence = robotic feel.
// With streaming: yield 4KB audio chunks as they arrive = browser plays immediately.
// This is the key to AVA feeling human and responsive.
//
// Implementations: AzureTtsService, KokoroTtsService
public interface ITextToSpeechService
{
    // Returns a stream of MP3 audio bytes (~4KB chunks)
    IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text,
        string voice = "en-US-JennyNeural",
        CancellationToken ct = default);
}
