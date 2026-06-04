using AVA.Application.Interfaces;
using AVA.Infrastructure.Settings;
using AVA.Infrastructure.Tts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AVA.UnitTests.Tts;

// Unit tests for AzureTextToSpeechService.
// Tests validate input guard clauses and streaming contract.
// Real Azure TTS synthesis is tested in IntegrationTests (Phase 5).
public class AzureTextToSpeechServiceTests
{
    private static AzureTextToSpeechService BuildService()
    {
        var settings = Options.Create(new AzureSettings
        {
            SpeechKey = "fake-key-unit-test",
            SpeechRegion = "eastus"
        });

        return new AzureTextToSpeechService(settings, NullLogger<AzureTextToSpeechService>.Instance);
    }

    // Helper: collects all yielded chunks from an IAsyncEnumerable into a list
    private static async Task<List<byte[]>> CollectChunksAsync(
        IAsyncEnumerable<byte[]> stream,
        CancellationToken ct = default)
    {
        var chunks = new List<byte[]>();
        await foreach (var chunk in stream.WithCancellation(ct))
            chunks.Add(chunk);
        return chunks;
    }

    // ── Test 1 ────────────────────────────────────────────────────────────
    // Empty string should yield zero audio chunks (guard clause)
    [Fact]
    public async Task SynthesizeStreamAsync_EmptyText_YieldsNoChunks()
    {
        var sut = BuildService();

        var chunks = await CollectChunksAsync(sut.SynthesizeStreamAsync(string.Empty));

        chunks.Should().BeEmpty("empty text produces no audio");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────
    // Whitespace-only string should yield zero chunks
    [Fact]
    public async Task SynthesizeStreamAsync_WhitespaceText_YieldsNoChunks()
    {
        var sut = BuildService();

        var chunks = await CollectChunksAsync(sut.SynthesizeStreamAsync("   "));

        chunks.Should().BeEmpty("whitespace-only text should be treated as empty");
    }

    // ── Test 3 ────────────────────────────────────────────────────────────
    // Null text should yield zero chunks without throwing
    [Fact]
    public async Task SynthesizeStreamAsync_NullText_YieldsNoChunks()
    {
        var sut = BuildService();

        var chunks = await CollectChunksAsync(sut.SynthesizeStreamAsync(null!));

        chunks.Should().BeEmpty("null text should be handled gracefully");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────
    // Interface contract - class must implement ITextToSpeechService
    [Fact]
    public void AzureTextToSpeechService_MustImplementInterface()
    {
        var sut = BuildService();

        sut.Should().BeAssignableTo<ITextToSpeechService>(
            "AzureTextToSpeechService must implement ITextToSpeechService");
    }

    // ── Test 5 ────────────────────────────────────────────────────────────
    // Already-cancelled token on empty text: should still return cleanly
    // (empty text guard fires before cancellation token is ever checked)
    [Fact]
    public async Task SynthesizeStreamAsync_EmptyText_CancelledToken_ReturnsCleanly()
    {
        var sut = BuildService();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CollectChunksAsync(
            sut.SynthesizeStreamAsync(string.Empty, ct: cts.Token), cts.Token);

        // Should not throw - empty guard fires before token is checked
        await act.Should().NotThrowAsync();
    }

    // ── Test 6 ────────────────────────────────────────────────────────────
    // Empty, whitespace, and null all behave the same way
    [Fact]
    public async Task SynthesizeStreamAsync_AllBlankVariants_AllYieldNoChunks()
    {
        var sut = BuildService();

        var r1 = await CollectChunksAsync(sut.SynthesizeStreamAsync(string.Empty));
        var r2 = await CollectChunksAsync(sut.SynthesizeStreamAsync("   "));
        var r3 = await CollectChunksAsync(sut.SynthesizeStreamAsync(null!));

        r1.Should().BeEmpty();
        r2.Should().BeEmpty();
        r3.Should().BeEmpty();
    }
}
