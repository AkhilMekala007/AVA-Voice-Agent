using AVA.Application.Interfaces;
using AVA.Application.Services;
using Microsoft.AspNetCore.SignalR;

namespace AVA.Api.Hubs;

// SignalR Hub - the real-time WebSocket endpoint for voice conversations.
//
// WHY SIGNALR AND NOT PLAIN HTTP?
// HTTP (request-response):
//   Client sends audio → waits → receives full MP3 → plays it
//   Problem: 3-6 second silence while waiting. Feels robotic.
//
// SignalR (WebSocket, persistent connection):
//   Client sends audio → server processes → pushes audio CHUNKS as they're ready
//   Client starts playing chunk 1 while chunk 2 is still being generated
//   Result: Sub-1-second perceived response time
//
// HOW SIGNALR HUBS WORK:
// - Client calls Hub methods by NAME (like RPC - Remote Procedure Call)
// - Hub calls client methods by NAME using Clients.Caller.SendAsync(...)
// - Connection is persistent (WebSocket) until client disconnects
//
// CLIENT → SERVER methods (client calls these):
//   SendAudio(byte[] audioBytes)   — sends raw audio for processing
//
// SERVER → CLIENT methods (hub calls these on the client):
//   ReceiveTranscript(string)      — "here's what you said"
//   ReceiveEmotion(string)         — "detected emotion: Lonely"
//   ReceiveAudioChunk(byte[])      — "here's a chunk of AVA's voice"
//   ReceiveAudioComplete()         — "all audio chunks sent, playback can finalize"
//   ReceiveError(string)           — "something went wrong"
public class VoiceHub : Hub
{
    private readonly ConversationOrchestrator _orchestrator;
    private readonly ITextToSpeechService _tts;
    private readonly ILogger<VoiceHub> _logger;

    public VoiceHub(
        ConversationOrchestrator orchestrator,
        ITextToSpeechService tts,
        ILogger<VoiceHub> logger)
    {
        _orchestrator = orchestrator;
        _tts = tts;
        _logger = logger;
    }

    // Called by the client when the user finishes speaking.
    // audioBytes = raw audio data (WAV/WebM from browser's MediaRecorder API)
    //
    // FLOW:
    // 1. Run full pipeline → get transcript + AVA's text response
    // 2. Push transcript back to client immediately (show captions)
    // 3. Push detected emotion (optional - for UI indicators)
    // 4. Stream TTS audio chunks back as they arrive from Azure
    // 5. Signal completion so the client knows to finalize playback
    public async Task SendAudio(byte[] audioBytes)
    {
        // Each SignalR connection gets a unique ConnectionId
        // We use it as sessionId so each browser tab gets its own conversation
        var sessionId = GetOrCreateSessionId();

        _logger.LogInformation(
            "[{ConnectionId}] Received {Bytes} bytes of audio",
            Context.ConnectionId, audioBytes.Length);

        try
        {
            // ── Run the full pipeline (STT → Emotion → Context → LLM) ──────
            var result = await _orchestrator.ProcessAsync(
                audioBytes,
                sessionId,
                ct: Context.ConnectionAborted);  // Cancel if client disconnects

            // If no speech was detected in the audio, tell the client
            if (!result.Success)
            {
                await Clients.Caller.SendAsync("ReceiveError",
                    "I couldn't hear anything clearly. Could you try again?",
                    Context.ConnectionAborted);
                return;
            }

            // ── Push transcript back so client can show captions ────────────
            // Client receives: { "type": "transcript", "text": "I feel really lonely" }
            await Clients.Caller.SendAsync(
                "ReceiveTranscript",
                result.Transcript,
                Context.ConnectionAborted);

            // ── Push detected emotion (optional UI indicator) ───────────────
            await Clients.Caller.SendAsync(
                "ReceiveEmotion",
                result.Emotion.ToString(),
                Context.ConnectionAborted);

            // ── Stream TTS audio chunks to client ───────────────────────────
            // Azure Neural TTS streams 4KB MP3 chunks.
            // Each chunk is pushed to the client immediately as it arrives.
            // WHY NOT COLLECT ALL CHUNKS FIRST?
            // Because that would add 1-3 seconds of silence.
            // Streaming means the browser can start AudioContext playback
            // as soon as the first 4KB chunk arrives.
            _logger.LogInformation(
                "[{ConnectionId}] Starting TTS stream for: \"{Response}\"",
                Context.ConnectionId, result.Response[..Math.Min(50, result.Response.Length)]);

            var chunkCount = 0;
            await foreach (var chunk in _tts.SynthesizeStreamAsync(
                result.Response,
                ct: Context.ConnectionAborted))
            {
                await Clients.Caller.SendAsync(
                    "ReceiveAudioChunk",
                    chunk,
                    Context.ConnectionAborted);
                chunkCount++;
            }

            // ── Signal that all audio has been sent ─────────────────────────
            // Client uses this to finalise the audio buffer and trigger playback
            await Clients.Caller.SendAsync(
                "ReceiveAudioComplete",
                Context.ConnectionAborted);

            _logger.LogInformation(
                "[{ConnectionId}] Pipeline complete. Sent {Chunks} audio chunks. Latency: {Ms}ms",
                Context.ConnectionId, chunkCount, result.LatencyMs);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-processing — this is normal, not an error
            _logger.LogInformation(
                "[{ConnectionId}] Client disconnected during processing", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{ConnectionId}] Unhandled error in SendAudio pipeline",
                Context.ConnectionId);

            // Send a user-friendly error message to the client
            await Clients.Caller.SendAsync(
                "ReceiveError",
                "Something went wrong on my end. I'm sorry — please try again.",
                Context.ConnectionAborted);
        }
    }

    // Called by the client to test the full pipeline with typed text (skips STT).
    // Useful for development testing without a microphone.
    // Flow: text → Emotion → Context → LLM → TTS → audio chunks back to client
    public async Task SendText(string text)
    {
        var sessionId = GetOrCreateSessionId();

        if (string.IsNullOrWhiteSpace(text))
        {
            await Clients.Caller.SendAsync("ReceiveError", "Text cannot be empty.", Context.ConnectionAborted);
            return;
        }

        _logger.LogInformation("[{ConnectionId}] SendText: \"{Text}\"", Context.ConnectionId, text);

        try
        {
            // Run the full pipeline (skipping STT)
            var result = await _orchestrator.ProcessTextAsync(
                text, sessionId, ct: Context.ConnectionAborted);

            // Send transcript back (what the user typed)
            await Clients.Caller.SendAsync("ReceiveTranscript", result.Transcript, Context.ConnectionAborted);

            // Send detected emotion
            await Clients.Caller.SendAsync("ReceiveEmotion", result.Emotion.ToString(), Context.ConnectionAborted);

            // Stream TTS audio chunks
            await foreach (var chunk in _tts.SynthesizeStreamAsync(result.Response, ct: Context.ConnectionAborted))
            {
                await Clients.Caller.SendAsync("ReceiveAudioChunk", chunk, Context.ConnectionAborted);
            }

            await Clients.Caller.SendAsync("ReceiveAudioComplete", Context.ConnectionAborted);

            _logger.LogInformation("[{ConnectionId}] SendText complete. Latency: {Ms}ms", Context.ConnectionId, result.LatencyMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ConnectionId}] Error in SendText", Context.ConnectionId);
            await Clients.Caller.SendAsync("ReceiveError", "Something went wrong: " + ex.Message, Context.ConnectionAborted);
        }
    }

    // Called by the client to end the conversation and free resources.
    public async Task EndSession()
    {
        var sessionId = GetOrCreateSessionId();
        _logger.LogInformation(
            "[{ConnectionId}] Session ended by client", Context.ConnectionId);

        // For now just log - Phase 4 will call _context.ClearSessionAsync(sessionId)
        await Clients.Caller.SendAsync("SessionEnded", Context.ConnectionAborted);
    }

    // SignalR lifecycle: called when a new WebSocket connection is established.
    public override Task OnConnectedAsync()
    {
        _logger.LogInformation(
            "Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    // SignalR lifecycle: called when the WebSocket connection closes.
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
            _logger.LogInformation("Client disconnected cleanly: {ConnectionId}", Context.ConnectionId);
        else
            _logger.LogWarning(exception, "Client disconnected with error: {ConnectionId}", Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }

    // Maps SignalR ConnectionId to a Guid session ID.
    // WHY? ConnectionId is a string like "abc123xyz". Our domain uses Guid.
    // We store the mapping in the SignalR connection's Items dictionary,
    // which persists for the lifetime of this connection.
    private Guid GetOrCreateSessionId()
    {
        if (Context.Items.TryGetValue("SessionId", out var existing) && existing is Guid id)
            return id;

        var newId = Guid.NewGuid();
        Context.Items["SessionId"] = newId;
        return newId;
    }
}
