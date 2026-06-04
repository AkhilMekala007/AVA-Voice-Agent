using AVA.Application.Interfaces;
using AVA.Infrastructure.Settings;
using AVA.Infrastructure.Stt;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AVA.UnitTests.Stt;

// Unit tests for AzureSpeechToTextService.
// We test ONLY the behaviour we can control without Azure:
//   - Input validation (empty/null audio)
//   - Interface contract (does it implement the right interface?)
// Real Azure transcription is tested in IntegrationTests (Phase 5).
//
// NullLogger.Instance is used instead of Mock<ILogger> to avoid
// Moq version compatibility issues with ILogger verification.
public class AzureSpeechToTextServiceTests
{
    // Build the service with fake settings (Azure is never called in these tests
    // because we return early before the Azure SDK is ever reached)
    private static AzureSpeechToTextService BuildService()
    {
        var settings = Options.Create(new AzureSettings
        {
            SpeechKey = "fake-key-unit-test",
            SpeechRegion = "eastus"
        });

        // NullLogger swallows all log calls silently - no mock needed
        return new AzureSpeechToTextService(settings, NullLogger<AzureSpeechToTextService>.Instance);
    }

    // ── Test 1 ────────────────────────────────────────────────────────────
    // Empty byte array should return empty string immediately (no Azure call)
    [Fact]
    public async Task TranscribeAsync_EmptyAudio_ReturnsEmptyString()
    {
        var sut = BuildService();

        var result = await sut.TranscribeAsync(Array.Empty<byte>());

        result.Should().BeEmpty("empty audio produces no transcription");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────
    // Null audio bytes should return empty string (guard clause check)
    [Fact]
    public async Task TranscribeAsync_NullAudio_ReturnsEmptyString()
    {
        var sut = BuildService();

        var result = await sut.TranscribeAsync(null!);

        result.Should().BeEmpty("null audio should be handled gracefully");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────
    // The class must implement the interface - catches missing ": ISpeechToTextService"
    [Fact]
    public void AzureSpeechToTextService_MustImplementInterface()
    {
        var sut = BuildService();

        sut.Should().BeAssignableTo<ISpeechToTextService>(
            "AzureSpeechToTextService must implement ISpeechToTextService");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────
    // Cancellation token should be respected - no exception leaks
    [Fact]
    public async Task TranscribeAsync_EmptyAudio_WithCancelledToken_ReturnsEmpty()
    {
        var sut = BuildService();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Empty audio returns before the cancellation token is ever checked,
        // so this should still return empty string cleanly
        var result = await sut.TranscribeAsync(Array.Empty<byte>(), cts.Token);

        result.Should().BeEmpty();
    }

    // ── Test 5 ────────────────────────────────────────────────────────────
    // Parameterized: both null and empty should behave the same way
    // NOTE: We use separate [Fact] tests instead of [Theory] with new byte[0]
    // because array creation in InlineData causes xUnit discovery issues.
    [Fact]
    public async Task TranscribeAsync_NullOrEmptyAudio_BothReturnEmpty()
    {
        var sut = BuildService();

        var resultForEmpty = await sut.TranscribeAsync(Array.Empty<byte>());
        var resultForNull  = await sut.TranscribeAsync(null!);

        resultForEmpty.Should().BeEmpty();
        resultForNull.Should().BeEmpty();
        resultForEmpty.Should().Be(resultForNull, "null and empty audio should behave identically");
    }
}
