using AVA.Application.DTOs;
using AVA.Domain.Enums;

namespace AVA.Application.Interfaces;

// Reads conversation history and saves new exchanges.
// Keeps only the last N turns to control LLM token costs.
// Implementations: InMemoryContextManager (Phase 3), SqliteContextManager (Phase 4)
public interface IContextManager
{
    // Retrieves conversation context (history + user metadata) for a session.
    // userName and userGoals are used by the system prompt builder to personalise responses.
    Task<ConversationContext> GetContextAsync(
        Guid sessionId,
        string userName = "Friend",
        string userGoals = "",
        CancellationToken ct = default);

    // Saves a completed user-AVA exchange. Called after every full round-trip.
    Task SaveExchangeAsync(
        Guid sessionId,
        string userText,
        string avaResponse,
        EmotionType detectedEmotion,
        CancellationToken ct = default);

    // Clears all history for a session when it ends. Frees memory/DB rows.
    Task ClearSessionAsync(Guid sessionId);
}
