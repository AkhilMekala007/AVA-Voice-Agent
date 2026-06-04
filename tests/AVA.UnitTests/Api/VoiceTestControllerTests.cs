using AVA.Api.Controllers;
using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Application.Services;
using AVA.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AVA.UnitTests.Api;

// Unit tests for VoiceTestController.cs
//
// WHY CONTROLLER NEEDS TESTS:
// VoiceTestController has INPUT VALIDATION logic:
//   - Transcribe() must return 400 if no file uploaded
//   - Transcribe() must return 400 if file is empty
//   - Synthesize() must return 400 if text is empty
//   - Synthesize() must call the TTS service and return audio
//
// NOTE ON ConversationOrchestrator:
// VoiceTestController now depends on ConversationOrchestrator (a concrete class).
// We can't mock it directly, so we build it with all-mock dependencies.
// The controller tests only test Transcribe/Synthesize/Ping — endpoints that
// do NOT call the orchestrator — so the orchestrator's mocks never fire.
public class VoiceTestControllerTests
{
    private readonly Mock<ISpeechToTextService> _mockStt;
    private readonly Mock<ITextToSpeechService> _mockTts;
    private readonly VoiceTestController _controller;

    public VoiceTestControllerTests()
    {
        _mockStt = new Mock<ISpeechToTextService>();
        _mockTts = new Mock<ITextToSpeechService>();

        // Build ConversationOrchestrator with fully-mocked dependencies.
        // The orchestrator is NOT called in the tests below (Transcribe/Synthesize/Ping
        // don't use it), so these mocks never need setup.
        var mockLlm     = new Mock<ILlmProvider>();
        var mockEmotion = new Mock<IEmotionAnalyzer>();
        var mockContext = new Mock<IContextManager>();
        var mockPrompt  = new Mock<ISystemPromptBuilder>();

        // Provide safe defaults so the orchestrator doesn't throw if accidentally called
        mockEmotion.Setup(e => e.Analyze(It.IsAny<string>())).Returns(EmotionType.Neutral);
        mockPrompt.Setup(p => p.Build(It.IsAny<ConversationContext>(), It.IsAny<EmotionType>()))
                  .Returns("test system prompt");
        mockContext.Setup(c => c.GetContextAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationContext(Guid.NewGuid(), new List<ChatMessage>()));
        mockLlm.Setup(l => l.StreamCompletionAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable("test response"));

        var orchestrator = new ConversationOrchestrator(
            _mockStt.Object,
            mockLlm.Object,
            mockEmotion.Object,
            mockContext.Object,
            mockPrompt.Object,
            NullLogger<ConversationOrchestrator>.Instance
        );

        _controller = new VoiceTestController(
            _mockStt.Object,
            _mockTts.Object,
            orchestrator,
            NullLogger<VoiceTestController>.Instance
        );
    }

    // ════════════════════════════════════════════════════════
    // TRANSCRIBE endpoint tests
    // ════════════════════════════════════════════════════════

    // ── Test 1 ────────────────────────────────────────────────────────────
    // Null audio file should return HTTP 400 Bad Request
    [Fact]
    public async Task Transcribe_NullFile_Returns400BadRequest()
    {
        var result = await _controller.Transcribe(null!, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "uploading no file should be rejected with 400");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────
    // Empty file (0 bytes) should return HTTP 400 Bad Request
    [Fact]
    public async Task Transcribe_EmptyFile_Returns400BadRequest()
    {
        // Create a fake IFormFile with 0 bytes
        var emptyFile = CreateFormFile(Array.Empty<byte>(), "empty.wav");

        var result = await _controller.Transcribe(emptyFile, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "uploading an empty file should be rejected with 400");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────
    // Valid file should call STT service and return 200 with transcript
    [Fact]
    public async Task Transcribe_ValidFile_CallsSttAndReturns200()
    {
        var audioBytes = new byte[] { 1, 2, 3, 4, 5 }; // Fake audio data
        var file = CreateFormFile(audioBytes, "audio.wav");

        // Tell the mock: "when TranscribeAsync is called, return this text"
        _mockStt
            .Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("I feel really motivated today");

        var result = await _controller.Transcribe(file, CancellationToken.None);

        // Verify it returned 200 OK
        result.Should().BeOfType<OkObjectResult>("valid audio should return 200 OK");

        // Verify STT was actually called once
        _mockStt.Verify(
            s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the STT service must be called exactly once per request");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────
    // When STT returns empty string, response should reflect failure gracefully
    [Fact]
    public async Task Transcribe_SttReturnsEmpty_Returns200WithSuccessFalse()
    {
        var file = CreateFormFile(new byte[] { 1, 2, 3 }, "silence.wav");

        // STT found no speech in the audio
        _mockStt
            .Setup(s => s.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var result = await _controller.Transcribe(file, CancellationToken.None);

        // Should still return 200 (not a server error - silence is valid input)
        result.Should().BeOfType<OkObjectResult>("no speech detected is not a server error");

        // The response body should indicate Success = false
        var ok = result as OkObjectResult;
        var body = ok!.Value!.ToString();
        body.Should().Contain("False", "Success should be false when transcript is empty");
    }

    // ════════════════════════════════════════════════════════
    // SYNTHESIZE endpoint tests
    // ════════════════════════════════════════════════════════

    // ── Test 5 ────────────────────────────────────────────────────────────
    // Empty text in request body should return 400 Bad Request
    [Fact]
    public async Task Synthesize_EmptyText_Returns400BadRequest()
    {
        var request = new SynthesizeRequest(string.Empty);

        var result = await _controller.Synthesize(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "empty text cannot be synthesized to audio");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────
    // Whitespace-only text should return 400 Bad Request
    [Fact]
    public async Task Synthesize_WhitespaceText_Returns400BadRequest()
    {
        var request = new SynthesizeRequest("   ");

        var result = await _controller.Synthesize(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>(
            "whitespace-only text should be treated as empty");
    }

    // ── Test 7 ────────────────────────────────────────────────────────────
    // Valid text should call TTS and return a FileResult (audio/mpeg)
    [Fact]
    public async Task Synthesize_ValidText_CallsTtsAndReturnsAudioFile()
    {
        var request = new SynthesizeRequest("Hello, I am AVA. I am here for you.");
        var fakeAudioChunk = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }; // Fake MP3 header bytes

        // Tell the mock TTS to yield one audio chunk
        _mockTts
            .Setup(t => t.SynthesizeStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable(fakeAudioChunk));

        var result = await _controller.Synthesize(request, CancellationToken.None);

        // Should return a file result (downloadable audio)
        result.Should().BeOfType<FileContentResult>(
            "valid text should produce a downloadable audio file");

        var fileResult = result as FileContentResult;
        fileResult!.ContentType.Should().Be("audio/wav",
            "the audio file must be in WAV format (changed from MP3 for universal browser compatibility)");
        fileResult.FileContents.Should().NotBeEmpty(
            "the audio file must contain actual bytes");

        // Verify TTS was called exactly once
        _mockTts.Verify(
            t => t.SynthesizeStreamAsync(
                request.Text,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "TTS service must be called exactly once with the request text");
    }

    // ── Test 8 ────────────────────────────────────────────────────────────
    // Ping endpoint should always return 200
    [Fact]
    public void Ping_Always_Returns200()
    {
        var result = _controller.Ping();

        result.Should().BeOfType<OkObjectResult>("ping is a health check and must always return 200");
    }

    // ════════════════════════════════════════════════════════
    // GREET endpoint tests
    // ════════════════════════════════════════════════════════

    // ── Test 9 ────────────────────────────────────────────────────────────
    // Greet should call TTS and return 200 with audioBase64 + greetingText
    [Fact]
    public async Task Greet_Always_CallsTtsAndReturns200WithAudio()
    {
        // Arrange — TTS returns a fake WAV audio chunk
        var fakeAudioChunk = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF" - fake WAV header
        _mockTts
            .Setup(t => t.SynthesizeStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable(fakeAudioChunk));

        // Act
        var result = await _controller.Greet(CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>("greet should always return 200");

        // TTS must have been called with a non-empty greeting text
        _mockTts.Verify(
            t => t.SynthesizeStreamAsync(
                It.Is<string>(s => !string.IsNullOrWhiteSpace(s)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "TTS must be called once to synthesize the greeting");

        // Response body must contain the expected fields
        var ok   = result as OkObjectResult;
        var body = ok!.Value!.ToString();
        body.Should().Contain("True",         "success should be true");
        body.Should().Contain("greetingText", "response must include the greeting text");
        body.Should().Contain("audioBase64",  "response must include base64-encoded audio");
    }

    // ── Test 10 ────────────────────────────────────────────────────────────
    // Greet should include a warm, welcoming message (not empty)
    [Fact]
    public async Task Greet_GreetingText_IsNotEmpty()
    {
        _mockTts
            .Setup(t => t.SynthesizeStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable(new byte[] { 0x00 }));

        var result = await _controller.Greet(CancellationToken.None);
        var ok     = result as OkObjectResult;

        // Use reflection to read the anonymous object's greetingText property
        var value       = ok!.Value!;
        var greetingProp = value.GetType().GetProperty("greetingText");
        greetingProp.Should().NotBeNull("greetingText property must exist in the response");

        var greetingText = greetingProp!.GetValue(value) as string;
        greetingText.Should().NotBeNullOrWhiteSpace(
            "AVA must always greet the user with a non-empty message");
        greetingText!.Length.Should().BeGreaterThan(20,
            "the greeting should be a meaningful sentence, not a placeholder");
    }

    // ════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════════════

    // Creates a fake IFormFile from raw bytes
    // IFormFile is an interface from ASP.NET Core that represents an uploaded file
    private static IFormFile CreateFormFile(byte[] content, string fileName)
    {
        var stream = new MemoryStream(content);
        return new FormFile(
            baseStream: stream,
            baseStreamOffset: 0,
            length: content.Length,
            name: "audioFile",
            fileName: fileName
        );
    }

    // Creates an IAsyncEnumerable<byte[]> from a single chunk (for TTS mock)
    private static async IAsyncEnumerable<byte[]> CreateAsyncEnumerable(byte[] chunk)
    {
        yield return chunk;
        await Task.CompletedTask;
    }

    // Creates an IAsyncEnumerable<string> from a single token (for LLM mock)
    private static async IAsyncEnumerable<string> CreateAsyncEnumerable(string token)
    {
        yield return token;
        await Task.CompletedTask;
    }
}
