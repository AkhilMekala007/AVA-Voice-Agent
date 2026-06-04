using AVA.Application.DTOs;

namespace AVA.Application.Interfaces;

// WHY STREAM TOKENS?
// Claude generates one word at a time. Streaming lets us trigger TTS as soon
// as the first SENTENCE is complete - user hears AVA while Claude still types.
// This is how we get sub-1.2s latency instead of 3-4s.
//
// Implementations: ClaudeLlmService, OllamaLlmService
public interface ILlmProvider
{
    IAsyncEnumerable<string> StreamCompletionAsync(
        IReadOnlyList<ChatMessage> history,
        string systemPrompt,
        CancellationToken ct = default);
}
