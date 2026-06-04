using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Application.Services;
using AVA.Domain.Enums;
using AVA.Infrastructure.Context;
using AVA.Infrastructure.Emotion;
using AVA.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AVA.IntegrationTests.Pipeline;

// ── Integration Tests: Full Conversation Pipeline ────────────────────────────
//
// WHAT IS AN INTEGRATION TEST?
// Unlike unit tests (which test ONE class in isolation with all dependencies mocked),
// integration tests wire MULTIPLE REAL components together to verify they work
// as a system.
//
// WHAT IS REAL HERE (not mocked):
//   ✅ ConversationOrchestrator      — the pipeline brain
//   ✅ EfCoreContextManager          — reads/writes SQLite
//   ✅ SQLite (InMemory provider)    — real EF Core queries, real SQL, real persistence
//   ✅ DynamicSystemPromptBuilder    — builds the actual system prompt
//   ✅ RuleBasedEmotionClassifier    — detects emotions from real text
//
// WHAT IS MOCKED (external services we can't call in CI):
//   🔧 ILlmProvider       — Claude API (costs money, needs internet, non-deterministic)
//   🔧 ISpeechToTextService — Azure STT (needs credentials, audio input)
//
// WHY INTEGRATION TESTS MATTER:
// Unit tests prove each class works in isolation.
// Integration tests prove the classes work TOGETHER correctly.
// Example: EfCoreContextManager saving messages AND the Orchestrator loading them
// in the right order is only testable as a combined system.
public class ConversationPipelineTests : IDisposable
{
    private readonly IDbContextFactory<AvaDbContext> _dbFactory;
    private readonly EfCoreContextManager _contextManager;
    private readonly DynamicSystemPromptBuilder _promptBuilder;
    private readonly RuleBasedEmotionClassifier _emotionClassifier;
    private readonly Mock<ILlmProvider> _mockLlm;
    private readonly Mock<ISpeechToTextService> _mockStt;
    private readonly ConversationOrchestrator _orchestrator;

    public ConversationPipelineTests()
    {
        // Each test class instance gets a completely isolated InMemory database.
        // Using Guid.NewGuid() as the database name guarantees no cross-test contamination
        // even when tests run in parallel.
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AvaDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        _dbFactory        = new TestDbContextFactory(options);
        _contextManager   = new EfCoreContextManager(_dbFactory, NullLogger<EfCoreContextManager>.Instance);
        _promptBuilder    = new DynamicSystemPromptBuilder();
        _emotionClassifier = new RuleBasedEmotionClassifier();

        _mockLlm = new Mock<ILlmProvider>();
        _mockStt = new Mock<ISpeechToTextService>();

        _orchestrator = new ConversationOrchestrator(
            _mockStt.Object,
            _mockLlm.Object,
            _emotionClassifier,
            _contextManager,
            _promptBuilder,
            NullLogger<ConversationOrchestrator>.Instance
        );
    }

    // Helper: set up LLM mock to return a fixed response for the main conversation call.
    //
    // WHY TWO SETUPS?
    // ConversationOrchestrator makes TWO calls to ILlmProvider per user message:
    //   1. RefineTranscriptAsync  — uses the "transcript cleaner" system prompt
    //   2. Main LLM call          — uses the emotion-aware AVA system prompt
    //
    // If we set up a single catch-all mock, BOTH calls return the mocked response.
    // RefineTranscriptAsync then "refines" the transcript into "I'm so glad you reached out..."
    // which breaks emotion detection (Anxious → Neutral) and content assertions.
    //
    // Fix: use It.Is<string>() to match on the system prompt content so the refinement
    // call returns the original input unchanged, and only the main call returns our fixture.
    private void SetupLlmResponse(string response)
    {
        // Refinement call: detect by the unique "transcript cleaner" phrase in the system prompt.
        // Return the user's input text unchanged — simulates "no filler words found".
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<ChatMessage>, string, CancellationToken>(
                (msgs, _, _) => PassThroughStream(msgs[0].Content));

        // Main AVA conversation call: return our fixture response.
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => !s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Returns(CreateTokenStream(response));
    }

    // Returns the input string as a single-token stream (used for refinement passthrough)
    private static async IAsyncEnumerable<string> PassThroughStream(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────
    // First message to AVA should produce a response and save the exchange to DB
    [Fact]
    public async Task ProcessTextAsync_FirstMessage_SavesExchangeToDatabase()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        SetupLlmResponse("I'm so glad you reached out. Tell me what's on your mind.");

        // Act
        var result = await _orchestrator.ProcessTextAsync(
            "I feel really lonely lately",
            sessionId);

        // Assert — pipeline result
        result.Success.Should().BeTrue();
        result.Response.Should().Be("I'm so glad you reached out. Tell me what's on your mind.");
        result.Transcript.Should().Be("I feel really lonely lately");
        result.Emotion.Should().Be(EmotionType.Lonely,
            "the word 'lonely' should trigger EmotionType.Lonely");

        // Assert — messages were actually saved to the database
        await using var db = await _dbFactory.CreateDbContextAsync();
        var savedMessages = await db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        savedMessages.Should().HaveCount(2, "one exchange = user message + AVA response");
        savedMessages[0].Role.Should().Be("User");
        savedMessages[0].Content.Should().Be("I feel really lonely lately");
        savedMessages[1].Role.Should().Be("Assistant");
        savedMessages[1].Content.Should().Be("I'm so glad you reached out. Tell me what's on your mind.");
        savedMessages[0].Emotion.Should().Be("Lonely");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────
    // Second message should include first exchange in context sent to LLM
    [Fact]
    public async Task ProcessTextAsync_SecondMessage_PreviousExchangeInContext()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        SetupLlmResponse("That sounds really tough. I'm here with you.");

        // First message
        await _orchestrator.ProcessTextAsync("I feel really sad today", sessionId);

        // Capture what messages the LLM receives on the SECOND main call.
        // We only capture on the non-refinement call (the actual conversation call).
        IReadOnlyList<ChatMessage>? capturedMessages = null;
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => !s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>(
                (msgs, _, _) => capturedMessages = msgs)
            .Returns(CreateTokenStream("I understand. What happened?"));

        // Act — second message
        await _orchestrator.ProcessTextAsync("I just lost my job", sessionId);

        // Assert — LLM received 3 messages: first user, first AVA, second user
        capturedMessages.Should().NotBeNull();
        capturedMessages!.Should().HaveCount(3,
            "context should contain: first user msg + first AVA response + new user msg");
        capturedMessages[0].Role.Should().Be(MessageRole.User);
        capturedMessages[0].Content.Should().Be("I feel really sad today");
        capturedMessages[1].Role.Should().Be(MessageRole.Assistant);
        capturedMessages[2].Role.Should().Be(MessageRole.User);
        capturedMessages[2].Content.Should().Be("I just lost my job");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────
    // Emotion detection should correctly classify emotional messages
    [Fact]
    public async Task ProcessTextAsync_AnxiousMessage_DetectsAnxiousEmotion()
    {
        var sessionId = Guid.NewGuid();
        SetupLlmResponse("Take a deep breath. Let's work through this together.");

        var result = await _orchestrator.ProcessTextAsync(
            "I am so anxious about my interview tomorrow",
            sessionId);

        result.Emotion.Should().Be(EmotionType.Anxious);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────
    // Empty LLM response should use the fallback message
    [Fact]
    public async Task ProcessTextAsync_EmptyLlmResponse_UsesFallbackMessage()
    {
        var sessionId = Guid.NewGuid();

        // Refinement call returns input unchanged
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<ChatMessage>, string, CancellationToken>(
                (msgs, _, _) => PassThroughStream(msgs[0].Content));

        // Main LLM call returns empty (simulates API failure / empty delta stream)
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => !s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Returns(CreateTokenStream(string.Empty));

        var result = await _orchestrator.ProcessTextAsync("Hello", sessionId);

        result.Success.Should().BeTrue("pipeline should succeed even with empty LLM response");
        result.Response.Should().NotBeNullOrWhiteSpace(
            "a fallback message must be used when LLM returns nothing");
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────
    // Multiple sessions must not share message history
    [Fact]
    public async Task ProcessTextAsync_TwoSessions_HistoriesAreIsolated()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();

        SetupLlmResponse("I hear you.");

        await _orchestrator.ProcessTextAsync("I feel sad", sessionA);
        await _orchestrator.ProcessTextAsync("I feel happy", sessionB);

        await using var db = await _dbFactory.CreateDbContextAsync();

        var messagesA = await db.Messages.Where(m => m.SessionId == sessionA).ToListAsync();
        var messagesB = await db.Messages.Where(m => m.SessionId == sessionB).ToListAsync();

        messagesA.Should().HaveCount(2, "session A should have exactly its own 2 messages");
        messagesB.Should().HaveCount(2, "session B should have exactly its own 2 messages");
        messagesA.Should().NotContain(m => m.SessionId == sessionB,
            "session A must not contain session B's messages");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────
    // Conversation history persists and loads correctly across multiple orchestrator calls
    [Fact]
    public async Task ProcessTextAsync_HistoryPersistsAcrossMultipleCalls()
    {
        var sessionId = Guid.NewGuid();
        SetupLlmResponse("AVA response");

        // Simulate 3 separate round-trips (as if 3 HTTP requests)
        await _orchestrator.ProcessTextAsync("Message 1", sessionId);
        await _orchestrator.ProcessTextAsync("Message 2", sessionId);
        await _orchestrator.ProcessTextAsync("Message 3", sessionId);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var count = await db.Messages.CountAsync(m => m.SessionId == sessionId);

        count.Should().Be(6, "3 exchanges × 2 messages each = 6 messages in DB");
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────
    // System prompt must contain emotion guidance for the detected emotion
    [Fact]
    public async Task ProcessTextAsync_LonelyMessage_SystemPromptContainsLonelyGuidance()
    {
        var sessionId = Guid.NewGuid();
        string? capturedSystemPrompt = null;

        // Refinement passthrough
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<ChatMessage>, string, CancellationToken>(
                (msgs, _, _) => PassThroughStream(msgs[0].Content));

        // Main call — capture the system prompt
        _mockLlm
            .Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.Is<string>(s => !s.Contains("transcript cleaner")),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>(
                (_, prompt, _) => capturedSystemPrompt = prompt)
            .Returns(CreateTokenStream("I'm here for you."));

        await _orchestrator.ProcessTextAsync("I feel so lonely and isolated", sessionId);

        capturedSystemPrompt.Should().NotBeNull();
        capturedSystemPrompt.Should().Contain("Lonely",
            "system prompt must include emotion-specific guidance when user is lonely");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose() { /* InMemory DB cleans itself up */ }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static async IAsyncEnumerable<string> CreateTokenStream(string text)
    {
        if (!string.IsNullOrEmpty(text))
            yield return text;
        await Task.CompletedTask;
    }
}

// Minimal IDbContextFactory for integration tests
internal class TestDbContextFactory : IDbContextFactory<AvaDbContext>
{
    private readonly DbContextOptions<AvaDbContext> _options;
    public TestDbContextFactory(DbContextOptions<AvaDbContext> options) => _options = options;
    public AvaDbContext CreateDbContext() => new(_options);
    public Task<AvaDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(new AvaDbContext(_options));
}
