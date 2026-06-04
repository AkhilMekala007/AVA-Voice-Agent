using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Domain.Enums;
using AVA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AVA.Infrastructure.Context;

// Persistent implementation of IContextManager using EF Core + SQLite.
//
// REPLACES: InMemoryContextManager (Phase 3) — which lost all history on server restart.
//
// KEY DESIGN DECISIONS:
//
// 1. IDbContextFactory<AvaDbContext> instead of AvaDbContext directly.
//    WHY? AvaDbContext is Scoped (one per HTTP request) but IContextManager
//    is registered as Singleton (one for app lifetime). Injecting a Scoped
//    service into a Singleton causes a "captive dependency" bug — the Scoped
//    DbContext gets held too long and becomes stale/corrupted.
//    Solution: inject IDbContextFactory (Singleton-safe) and create a fresh
//    short-lived DbContext for each operation. This is the official EF Core
//    pattern for long-lived services.
//
// 2. MaxHistoryLength = 20 messages (10 turns).
//    WHY? Claude charges per token. 20 messages ≈ 2000-3000 tokens of history.
//    More history = richer responses but higher cost. 20 is a good balance.
//    We trim at save time, not at read time, so the DB stays lean.
//
// 3. await using var db = ... (IAsyncDisposable)
//    WHY? DbContext holds a database connection. We must dispose it promptly
//    after each operation to return the connection to the pool. 'await using'
//    is the async-safe version of 'using'.
public class EfCoreContextManager : IContextManager
{
    private readonly IDbContextFactory<AvaDbContext> _factory;
    private readonly ILogger<EfCoreContextManager> _logger;
    private const int MaxHistoryLength = 20; // max messages stored per session

    public EfCoreContextManager(
        IDbContextFactory<AvaDbContext> factory,
        ILogger<EfCoreContextManager> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    // Returns the last MaxHistoryLength messages for this session, in order.
    // Creates the session row if it doesn't exist yet.
    public async Task<ConversationContext> GetContextAsync(
        Guid sessionId,
        string userName = "Friend",
        string userGoals = "",
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Get or create the session row
        var session = await db.Sessions.FindAsync([sessionId], ct);
        if (session is null)
        {
            session = new ConversationSession
            {
                Id        = sessionId,
                UserName  = userName,
                UserGoals = userGoals
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[{SessionId}] New session created for user '{UserName}'", sessionId, userName);
        }
        else if (session.UserName == "Friend" && userName != "Friend")
        {
            // Update user name if we now know it (e.g. extracted from first message)
            session.UserName  = userName;
            session.UserGoals = userGoals;
            await db.SaveChangesAsync(ct);
        }

        // Load the most recent N messages, ordered by insertion (Id ASC)
        var rawMessages = await db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        // ── Cross-session memory lookup ──────────────────────────────────────
        // If the current session has NO messages but we know the user's name,
        // search for a previous session belonging to this user and import its history.
        //
        // WHY THIS IS NEEDED:
        // Each browser tab generates a new sessionId (stored in localStorage).
        // If the user opens a new tab, or clears localStorage, or used the app before
        // localStorage was added, their old messages are under a different sessionId.
        // By linking sessions via UserName we restore their history seamlessly.
        //
        // HOW IT WORKS:
        // 1. Find the most recent OTHER session where UserName matches
        // 2. Copy those messages into the current session (re-creates them with new SessionId)
        // 3. Mark the old session as migrated (rename it) to prevent double-import
        if (rawMessages.Count == 0 && userName != "Friend")
        {
            var previousSession = await db.Sessions
                .Where(s => s.UserName == userName
                         && s.Id != sessionId
                         && !s.UserGoals.StartsWith("[migrated]"))
                .OrderByDescending(s => s.LastActiveAt)
                .FirstOrDefaultAsync(ct);

            if (previousSession != null)
            {
                _logger.LogInformation(
                    "[{SessionId}] Found previous session {OldId} for user '{Name}' — importing history",
                    sessionId, previousSession.Id, userName);

                var previousMessages = await db.Messages
                    .Where(m => m.SessionId == previousSession.Id)
                    .OrderBy(m => m.Id)
                    .ToListAsync(ct);

                // Copy messages into the current session
                foreach (var msg in previousMessages)
                {
                    db.Messages.Add(new ConversationMessage
                    {
                        SessionId = sessionId,
                        Role      = msg.Role,
                        Content   = msg.Content,
                        Emotion   = msg.Emotion,
                        CreatedAt = msg.CreatedAt
                    });
                }

                // Mark old session as migrated so it's not imported again
                previousSession.UserGoals = "[migrated] " + previousSession.UserGoals;

                // Update current session with known user info
                session.UserName     = userName;
                session.LastActiveAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);

                // Reload messages now that they've been copied over
                rawMessages = await db.Messages
                    .Where(m => m.SessionId == sessionId)
                    .OrderBy(m => m.Id)
                    .ToListAsync(ct);

                _logger.LogInformation(
                    "[{SessionId}] Imported {Count} messages from previous session",
                    sessionId, rawMessages.Count);
            }
        }

        // Keep only the tail — drop the oldest if over the limit
        var trimmed = rawMessages.Count > MaxHistoryLength
            ? rawMessages.Skip(rawMessages.Count - MaxHistoryLength).ToList()
            : rawMessages;

        // Map Infrastructure entities → Application DTOs
        // WHY MAP? Application layer must not reference Infrastructure types.
        // This keeps the dependency flow clean: Application → Domain only.
        var chatMessages = trimmed.Select(m => new ChatMessage(
            Role    : Enum.Parse<MessageRole>(m.Role, ignoreCase: true),
            Content : m.Content
        )).ToList();

        _logger.LogInformation("[{SessionId}] GetContext: {Count} messages loaded", sessionId, chatMessages.Count);

        return new ConversationContext(sessionId, chatMessages, session.UserName, session.UserGoals);
    }

    // Saves one user-AVA exchange (2 messages) and trims history if over MaxHistoryLength.
    public async Task SaveExchangeAsync(
        Guid sessionId,
        string userText,
        string avaResponse,
        EmotionType detectedEmotion,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var now      = DateTime.UtcNow;
        var emotion  = detectedEmotion.ToString();

        // Add user message
        db.Messages.Add(new ConversationMessage
        {
            SessionId = sessionId,
            Role      = MessageRole.User.ToString(),
            Content   = userText,
            Emotion   = emotion,
            CreatedAt = now
        });

        // Add AVA's response
        db.Messages.Add(new ConversationMessage
        {
            SessionId = sessionId,
            Role      = MessageRole.Assistant.ToString(),
            Content   = avaResponse,
            Emotion   = emotion,
            CreatedAt = now.AddTicks(1) // ensure ordering: AVA always after user
        });

        // Update LastActiveAt on the session
        var session = await db.Sessions.FindAsync([sessionId], ct);
        if (session is not null)
            session.LastActiveAt = now;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[{SessionId}] Exchange saved. Emotion={Emotion}", sessionId, emotion);

        // Trim: if we've exceeded MaxHistoryLength, delete the oldest messages
        await TrimHistoryAsync(db, sessionId, ct);
    }

    // Deletes all messages and the session row for a given sessionId.
    // Called when a user explicitly ends a session (Phase 5: session management).
    //
    // WHY EXPLICIT MESSAGE DELETE (not relying on cascade)?
    // EF Core's InMemory provider (used in unit tests) does NOT support cascade deletes —
    // that's a database-level feature only available in real SQL engines like SQLite/Postgres.
    // Explicitly deleting messages first makes ClearSessionAsync work correctly in BOTH
    // unit tests (InMemory) and production (SQLite). Always prefer explicit over implicit
    // when the behaviour needs to be testable.
    public async Task ClearSessionAsync(Guid sessionId)
    {
        await using var db = await _factory.CreateDbContextAsync();

        // Step 1: Explicitly delete all messages for this session
        var messages = await db.Messages
            .Where(m => m.SessionId == sessionId)
            .ToListAsync();
        db.Messages.RemoveRange(messages);

        // Step 2: Delete the session row itself
        var session = await db.Sessions.FindAsync(sessionId);
        if (session is not null)
            db.Sessions.Remove(session);

        await db.SaveChangesAsync();
        _logger.LogInformation("[{SessionId}] Session and {Count} messages cleared from database",
            sessionId, messages.Count);
    }

    // Keeps the database lean by removing oldest messages when over MaxHistoryLength.
    //
    // WHY TRIM IN THE DB (not just in memory at read time)?
    // If we only trim at read time, the DB grows unboundedly.
    // A long conversation would eventually hold thousands of messages per session.
    // Trimming at save time caps the DB at MaxHistoryLength rows per session.
    private static async Task TrimHistoryAsync(AvaDbContext db, Guid sessionId, CancellationToken ct)
    {
        var messageCount = await db.Messages
            .CountAsync(m => m.SessionId == sessionId, ct);

        if (messageCount <= MaxHistoryLength) return;

        // Find the IDs of the oldest (excess) messages and delete them
        var excess    = messageCount - MaxHistoryLength;
        var toDelete  = await db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .Take(excess)
            .ToListAsync(ct);

        db.Messages.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
    }
}
