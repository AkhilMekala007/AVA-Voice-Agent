using System.Collections.Concurrent;
using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AVA.Application.Services;

// Stores conversation history per session in RAM.
//
// WHY IN-MEMORY FOR NOW?
// Phase 4 will add SQLite persistence (history survives server restarts).
// For Phase 3, in-memory is:
//   - Zero configuration (no DB setup, no migrations)
//   - Zero latency (no disk I/O)
//   - Enough to prove the full conversation loop works
//
// Because IContextManager is an interface, Phase 4 will create
// SqliteContextManager and swap it in Program.cs — no other code changes needed.
//
// WHY ConcurrentDictionary?
// In production, multiple users connect simultaneously via SignalR.
// If we used a plain Dictionary, two users accessing context at the same time
// would cause a race condition and corrupt data.
// ConcurrentDictionary uses fine-grained locks internally — thread-safe by design.
//
// DATA STRUCTURE:
// sessionId (Guid) → List<ChatMessage> (conversation history for that session)
// Each ChatMessage has: Role (User/Assistant) + Content (the text)
public class InMemoryContextManager : IContextManager
{
    // Thread-safe dictionary: one entry per active session
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _sessions = new();
    private readonly ILogger<InMemoryContextManager> _logger;

    // How many messages to keep in context window.
    // WHY 20? Claude's context window is large (200k tokens), but we only need
    // recent history for a coherent conversation. 20 messages ≈ 10 exchanges.
    // Keeping the context small also keeps costs down.
    private const int MaxHistoryLength = 20;

    public InMemoryContextManager(ILogger<InMemoryContextManager> logger)
    {
        _logger = logger;
    }

    // Retrieves the conversation context for a given session.
    // Creates a new empty context if this sessionId has never been seen.
    //
    // WHY RETURN ConversationContext (not just List<ChatMessage>)?
    // ConversationContext bundles history + user metadata (name, goals).
    // This gives the prompt builder everything it needs in one object.
    public Task<ConversationContext> GetContextAsync(
        Guid sessionId,
        string userName = "Friend",
        string userGoals = "",
        CancellationToken ct = default)
    {
        // GetOrAdd is atomic - thread-safe way to create if missing
        var history = _sessions.GetOrAdd(sessionId, _ => new List<ChatMessage>());

        _logger.LogDebug(
            "Retrieved context for session {SessionId}: {MessageCount} messages",
            sessionId, history.Count);

        // Return a snapshot (ToList) so the caller's copy isn't affected
        // if history is modified concurrently
        var context = new ConversationContext(
            SessionId: sessionId,
            Messages: history.ToList(),   // immutable snapshot
            UserName: userName,
            UserGoals: userGoals
        );

        return Task.FromResult(context);
    }

    // Saves a completed user-AVA exchange (one user message + one AVA response).
    // Called by ConversationOrchestrator after every full round-trip.
    //
    // WHY SAVE BOTH MESSAGES AT ONCE?
    // Claude's conversation format requires alternating User/Assistant messages.
    // If we only saved the user message, Claude would see two User messages in a row
    // on the next turn and produce unexpected behavior.
    public Task SaveExchangeAsync(
        Guid sessionId,
        string userMessage,
        string assistantMessage,
        Domain.Enums.EmotionType detectedEmotion,
        CancellationToken ct = default)
    {
        var history = _sessions.GetOrAdd(sessionId, _ => new List<ChatMessage>());

        lock (history) // Fine-grained lock on just this session's history
        {
            history.Add(new ChatMessage(Domain.Enums.MessageRole.User, userMessage));
            history.Add(new ChatMessage(Domain.Enums.MessageRole.Assistant, assistantMessage));

            // Trim to keep only the most recent MaxHistoryLength messages.
            // This prevents unbounded memory growth for long conversations.
            // We remove from the front (oldest messages first).
            while (history.Count > MaxHistoryLength)
                history.RemoveAt(0);
        }

        _logger.LogDebug(
            "Saved exchange for session {SessionId}. Emotion: {Emotion}. History: {Count} msgs",
            sessionId, detectedEmotion, history.Count);

        return Task.CompletedTask;
    }

    // Clears conversation history for a session (called when session ends).
    // Frees RAM for completed conversations.
    public Task ClearSessionAsync(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogInformation("Cleared context for session {SessionId}", sessionId);
        return Task.CompletedTask;
    }
}
