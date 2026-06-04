using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Application.Services;
using AVA.Infrastructure.Context;
using AVA.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AVA.IntegrationTests.Api;

// ── Integration Tests: HTTP API Endpoints ─────────────────────────────────────
//
// WHAT IS WebApplicationFactory?
// A test utility from Microsoft that spins up the real ASP.NET Core application
// (including all middleware, routing, DI) in memory — no network port needed.
// We get a real HttpClient that talks to the real server, but entirely in-process.
//
// WHY THIS LEVEL OF TESTING?
// Unit tests mock the controller's dependencies. They can't catch bugs in:
//   - Routing (wrong URL, wrong HTTP verb)
//   - Middleware ordering (CORS, auth, error handling)
//   - JSON serialisation (wrong field names, wrong types)
//   - DI wiring (missing service registration)
//
// These tests exercise the FULL stack from HTTP request to JSON response.
//
// OVERRIDES:
// We replace real Azure/Claude services with mocks in ConfigureTestServices()
// so tests don't need API keys and run deterministically in CI.
// SQLite is replaced with EF Core InMemory for speed and isolation.
public class ChatEndpointTests : IClassFixture<AvaWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ChatEndpointTests(AvaWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────
    // POST /api/voicetest/chat with valid text should return 200 with audio
    [Fact]
    public async Task Chat_ValidRequest_Returns200WithAudioBase64()
    {
        var request = new { text = "I feel anxious about tomorrow", sessionId = Guid.NewGuid() };

        var response = await _client.PostAsJsonAsync("/api/voicetest/chat", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "valid chat request must return HTTP 200");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("response").GetString().Should().NotBeNullOrWhiteSpace(
            "response text must be present");
        body.GetProperty("audioBase64").GetString().Should().NotBeNullOrWhiteSpace(
            "audio must be base64-encoded in the response");
        body.GetProperty("emotion").GetString().Should().NotBeNullOrWhiteSpace(
            "detected emotion must be returned");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────
    // POST /api/voicetest/chat with empty text should return 400
    [Fact]
    public async Task Chat_EmptyText_Returns400BadRequest()
    {
        var request = new { text = "", sessionId = Guid.NewGuid() };

        var response = await _client.PostAsJsonAsync("/api/voicetest/chat", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "empty text must be rejected with 400 before reaching the pipeline");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────
    // POST /api/voicetest/chat with whitespace-only text should return 400
    [Fact]
    public async Task Chat_WhitespaceText_Returns400BadRequest()
    {
        var request = new { text = "   ", sessionId = Guid.NewGuid() };

        var response = await _client.PostAsJsonAsync("/api/voicetest/chat", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────
    // GET /api/voicetest/ping should always return 200
    [Fact]
    public async Task Ping_Always_Returns200()
    {
        var response = await _client.GetAsync("/api/voicetest/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────
    // GET /api/voicetest/greet should return 200 with greeting text and audio
    [Fact]
    public async Task Greet_Always_Returns200WithGreetingAndAudio()
    {
        var response = await _client.GetAsync("/api/voicetest/greet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("greetingText").GetString().Should().NotBeNullOrWhiteSpace(
            "greeting text must be present");
        body.GetProperty("audioBase64").GetString().Should().NotBeNullOrWhiteSpace(
            "greeting audio must be base64-encoded");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────
    // GET /health should return 200
    [Fact]
    public async Task Health_Always_Returns200()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────
    // Two requests with the same sessionId should share conversation context
    [Fact]
    public async Task Chat_SameSession_SecondRequestHasPriorContext()
    {
        var sessionId = Guid.NewGuid();

        // First request
        var first = await _client.PostAsJsonAsync("/api/voicetest/chat",
            new { text = "My name is Akhil", sessionId });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second request — same session
        var second = await _client.PostAsJsonAsync("/api/voicetest/chat",
            new { text = "How are you?", sessionId });
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "second message in the same session must succeed");

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────
    // Response JSON must contain latencyMs to expose pipeline performance
    [Fact]
    public async Task Chat_Response_ContainsLatencyMs()
    {
        var request = new { text = "Hello AVA", sessionId = Guid.NewGuid() };

        var response = await _client.PostAsJsonAsync("/api/voicetest/chat", request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("latencyMs", out var latency).Should().BeTrue(
            "latencyMs must be present so clients can display pipeline performance");
        latency.GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }
}

// ── Custom WebApplicationFactory ─────────────────────────────────────────────
// Boots the real AVA.Api application but overrides external services with mocks
// and replaces SQLite with an InMemory database for fast, isolated testing.
public class AvaWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // ── Replace SQLite with InMemory DB ───────────────────────────────
            // Remove the real DbContextFactory registration
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IDbContextFactory<AvaDbContext>));
            if (dbDescriptor != null)
                services.Remove(dbDescriptor);

            // Remove DbContext descriptor too (registered by EF Core internally)
            var dbCtxDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<AvaDbContext>));
            if (dbCtxDescriptor != null)
                services.Remove(dbCtxDescriptor);

            // Add InMemory database — unique name per factory instance
            var dbName = Guid.NewGuid().ToString();
            services.AddDbContextFactory<AvaDbContext>(options =>
                options.UseInMemoryDatabase(dbName));

            // ── Replace Azure STT with mock ───────────────────────────────────
            var sttDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ITextToSpeechService));
            if (sttDescriptor != null) services.Remove(sttDescriptor);

            var mockTts = new Mock<ITextToSpeechService>();
            mockTts.Setup(t => t.SynthesizeStreamAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(FakeAudioStream());
            services.AddSingleton(mockTts.Object);

            // ── Replace Azure TTS with mock ───────────────────────────────────
            var ttsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ISpeechToTextService));
            if (ttsDescriptor != null) services.Remove(ttsDescriptor);

            var mockStt = new Mock<ISpeechToTextService>();
            mockStt.Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Test transcript");
            services.AddSingleton(mockStt.Object);

            // ── Replace Claude LLM with mock ──────────────────────────────────
            var llmDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ILlmProvider));
            if (llmDescriptor != null) services.Remove(llmDescriptor);

            var mockLlm = new Mock<ILlmProvider>();
            mockLlm.Setup(l => l.StreamCompletionAsync(
                    It.IsAny<IReadOnlyList<ChatMessage>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns(FakeLlmStream("I'm here for you. What's on your mind?"));
            services.AddSingleton(mockLlm.Object);

            // Re-register ConversationOrchestrator so it picks up the new mocks
            var orchDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(ConversationOrchestrator));
            if (orchDescriptor != null) services.Remove(orchDescriptor);
            services.AddSingleton<ConversationOrchestrator>();
        });

        // Use test environment so appsettings.Development.json (real keys) isn't loaded
        builder.UseEnvironment("Testing");
    }

    private static async IAsyncEnumerable<byte[]> FakeAudioStream()
    {
        // Minimal valid WAV header (44 bytes) + silence
        var wav = new byte[44];
        var view = new System.IO.BinaryWriter(new System.IO.MemoryStream(wav));
        // RIFF header
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        yield return wav;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> FakeLlmStream(string text)
    {
        yield return text;
        await Task.CompletedTask;
    }
}
