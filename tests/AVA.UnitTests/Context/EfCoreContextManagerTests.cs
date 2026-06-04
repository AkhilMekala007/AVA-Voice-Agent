using AVA.Application.DTOs;
using AVA.Domain.Enums;
using AVA.Infrastructure.Context;
using AVA.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVA.UnitTests.Context;

// Unit tests for EfCoreContextManager.
//
// WHY EF CORE INMEMORY PROVIDER (not SQLite)?
// Unit tests must be:
//   - Fast      : no disk I/O, no file locking
//   - Isolated  : each test gets a fresh empty database
//   - Parallel  : different test instances must not share state
//
// EF Core InMemory provider stores data in a simple dictionary in memory.
// It's the official EF Core testing tool.
//
// WHY NOT MOCK THE DbContext?
// Mocking DbContext is notoriously painful and error-prone.
// The InMemory provider lets us test real EF Core query logic (LINQ, ordering,
// navigation properties) without touching disk — the best of both worlds.
//
// DATABASE NAME: each test method uses a unique Guid as the database name.
// This guarantees complete isolation even if tests run in parallel.
public class EfCoreContextManagerTests
{
    // Creates a fresh InMemory AvaDbContext for each call.
    // dbName must be unique per test to prevent state leakage.
    private static IDbContextFactory<AvaDbContext> CreateFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<AvaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new TestDbContextFactory(options);
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────
    // First call to GetContextAsync should create the session and return empty history
    [Fact]
    public async Task GetContextAsync_NewSession_CreatesSessionAndReturnsEmptyHistory()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        var context = await manager.GetContextAsync(sessionId, "Akhil", "Become more confident");

        context.SessionId.Should().Be(sessionId);
        context.UserName.Should().Be("Akhil");
        context.UserGoals.Should().Be("Become more confident");
        context.Messages.Should().BeEmpty("brand new session has no history");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────
    // Saving an exchange then loading context should return both messages in order
    [Fact]
    public async Task SaveExchangeAsync_ThenGetContext_ReturnsSavedMessages()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        // Ensure session row exists first
        await manager.GetContextAsync(sessionId);

        // Save one exchange
        await manager.SaveExchangeAsync(
            sessionId,
            userText:      "I feel really lonely today",
            avaResponse:   "I'm here for you. Tell me more about what you're feeling.",
            detectedEmotion: EmotionType.Lonely);

        // Reload context
        var context = await manager.GetContextAsync(sessionId);

        context.Messages.Should().HaveCount(2,
            "one exchange = user message + AVA response = 2 messages");

        context.Messages[0].Role.Should().Be(MessageRole.User,
            "first message should be from the user");
        context.Messages[0].Content.Should().Be("I feel really lonely today");

        context.Messages[1].Role.Should().Be(MessageRole.Assistant,
            "second message should be AVA's response");
        context.Messages[1].Content.Should().Be(
            "I'm here for you. Tell me more about what you're feeling.");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────
    // Multiple exchanges should accumulate in order
    [Fact]
    public async Task SaveExchangeAsync_MultipleExchanges_MessagesAccumulateInOrder()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        await manager.GetContextAsync(sessionId);

        await manager.SaveExchangeAsync(sessionId, "Turn 1 user", "Turn 1 AVA", EmotionType.Neutral);
        await manager.SaveExchangeAsync(sessionId, "Turn 2 user", "Turn 2 AVA", EmotionType.Sad);
        await manager.SaveExchangeAsync(sessionId, "Turn 3 user", "Turn 3 AVA", EmotionType.Anxious);

        var context = await manager.GetContextAsync(sessionId);

        context.Messages.Should().HaveCount(6, "3 exchanges × 2 messages each = 6");
        context.Messages[0].Content.Should().Be("Turn 1 user");
        context.Messages[4].Content.Should().Be("Turn 3 user");
        context.Messages[5].Content.Should().Be("Turn 3 AVA");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────
    // Saving more than MaxHistoryLength messages should trim the oldest ones
    [Fact]
    public async Task SaveExchangeAsync_OverMaxHistory_TrimsOldestMessages()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        await manager.GetContextAsync(sessionId);

        // MaxHistoryLength = 20 messages = 10 exchanges
        // Save 12 exchanges (24 messages) to force trimming
        for (int i = 1; i <= 12; i++)
            await manager.SaveExchangeAsync(sessionId, $"User {i}", $"AVA {i}", EmotionType.Neutral);

        var context = await manager.GetContextAsync(sessionId);

        // Should be trimmed to MaxHistoryLength (20)
        context.Messages.Count.Should().BeLessThanOrEqualTo(20,
            "history must not exceed MaxHistoryLength");

        // The NEWEST messages should be kept, not the oldest
        context.Messages.Last().Content.Should().Be("AVA 12",
            "most recent AVA response must always be in the window");
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────
    // ClearSessionAsync should delete all messages for the session
    [Fact]
    public async Task ClearSessionAsync_DeletesAllMessagesForSession()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        await manager.GetContextAsync(sessionId);
        await manager.SaveExchangeAsync(sessionId, "Hello", "Hi there!", EmotionType.Neutral);

        // Confirm messages exist
        var before = await manager.GetContextAsync(sessionId);
        before.Messages.Should().HaveCount(2);

        // Clear the session
        await manager.ClearSessionAsync(sessionId);

        // After clearing, a new call should return empty history (fresh session)
        var after = await manager.GetContextAsync(sessionId);
        after.Messages.Should().BeEmpty("cleared session should have no history");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────
    // Two different sessions must not share message history
    [Fact]
    public async Task GetContextAsync_TwoSessions_HistoriesAreIsolated()
    {
        var factory    = CreateFactory(Guid.NewGuid().ToString());
        var manager    = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionA   = Guid.NewGuid();
        var sessionB   = Guid.NewGuid();

        await manager.GetContextAsync(sessionA);
        await manager.GetContextAsync(sessionB);

        // Save to session A only
        await manager.SaveExchangeAsync(sessionA, "Session A message", "Session A response", EmotionType.Neutral);

        var contextA = await manager.GetContextAsync(sessionA);
        var contextB = await manager.GetContextAsync(sessionB);

        contextA.Messages.Should().HaveCount(2, "session A should have its 2 messages");
        contextB.Messages.Should().BeEmpty("session B must not see session A's messages");
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────
    // GetContextAsync called twice for the same session should not duplicate history
    [Fact]
    public async Task GetContextAsync_CalledTwice_DoesNotDuplicateHistory()
    {
        var factory   = CreateFactory(Guid.NewGuid().ToString());
        var manager   = new EfCoreContextManager(factory, NullLogger<EfCoreContextManager>.Instance);
        var sessionId = Guid.NewGuid();

        await manager.SaveExchangeAsync(sessionId, "Hello", "Hi", EmotionType.Neutral);

        // Calling GetContextAsync twice should return the same 2 messages each time
        var first  = await manager.GetContextAsync(sessionId);
        var second = await manager.GetContextAsync(sessionId);

        first.Messages.Should().HaveCount(2);
        second.Messages.Should().HaveCount(2,
            "repeated GetContext calls must not duplicate messages");
    }
}

// ── Test helper: IDbContextFactory implementation ────────────────────────────
// EF Core's IDbContextFactory is an interface — we need a concrete implementation
// for testing. This thin wrapper creates new DbContext instances from fixed options.
internal class TestDbContextFactory : IDbContextFactory<AvaDbContext>
{
    private readonly DbContextOptions<AvaDbContext> _options;

    public TestDbContextFactory(DbContextOptions<AvaDbContext> options)
        => _options = options;

    public AvaDbContext CreateDbContext()
        => new AvaDbContext(_options);

    public Task<AvaDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(new AvaDbContext(_options));
}
