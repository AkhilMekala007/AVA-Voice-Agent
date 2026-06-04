using AVA.Application.Interfaces;
using AVA.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AVA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VoiceTestController : ControllerBase
{
    private readonly ISpeechToTextService _stt;
    private readonly ITextToSpeechService _tts;
    private readonly ConversationOrchestrator _orchestrator;
    private readonly ILogger<VoiceTestController> _logger;

    public VoiceTestController(
        ISpeechToTextService stt,
        ITextToSpeechService tts,
        ConversationOrchestrator orchestrator,
        ILogger<VoiceTestController> logger)
    {
        _stt = stt;
        _tts = tts;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // POST /api/voicetest/transcribe
    [HttpPost("transcribe")]
    public async Task<IActionResult> Transcribe(IFormFile audioFile, CancellationToken ct)
    {
        if (audioFile is null || audioFile.Length == 0)
            return BadRequest(new { Error = "Upload a WAV audio file." });

        using var ms = new MemoryStream();
        await audioFile.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var transcript = await _stt.TranscribeAsync(bytes, ct);
        return Ok(new { Transcript = transcript, AudioBytes = bytes.Length, Success = !string.IsNullOrEmpty(transcript) });
    }

    // POST /api/voicetest/synthesize
    [HttpPost("synthesize")]
    [Produces("audio/wav")]
    public async Task<IActionResult> Synthesize([FromBody] SynthesizeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { Error = "Text cannot be empty." });

        var buffer = new MemoryStream();
        await foreach (var chunk in _tts.SynthesizeStreamAsync(req.Text, ct: ct))
            await buffer.WriteAsync(chunk, ct);

        return File(buffer.ToArray(), "audio/wav", "ava_response.wav");
    }

    // POST /api/voicetest/chat
    // Full pipeline: text → Claude → base64 WAV audio in JSON response.
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { Error = "Text cannot be empty." });

        var sessionId = req.SessionId == Guid.Empty ? Guid.NewGuid() : req.SessionId;

        _logger.LogInformation("Chat endpoint: session={SessionId}, text={Text}", sessionId, req.Text);

        var result = await _orchestrator.ProcessTextAsync(req.Text, sessionId, ct: ct);

        if (!result.Success || string.IsNullOrEmpty(result.Response))
            return Ok(new { result.Success, Message = "No response generated." });

        var buffer = new MemoryStream();
        await foreach (var chunk in _tts.SynthesizeStreamAsync(result.Response, ct: ct))
            await buffer.WriteAsync(chunk, ct);

        // Return JSON with base64-encoded audio.
        // WHY NOT HTTP headers? Headers only allow ASCII — Claude's responses
        // contain em dashes, curly quotes etc. (non-ASCII) which cause 500 errors.
        // Base64-encoding the audio in JSON is universally safe.
        return Ok(new
        {
            success   = true,
            transcript = result.Transcript,
            response  = result.Response,
            emotion   = result.Emotion.ToString(),
            latencyMs = result.LatencyMs,
            audioBase64  = Convert.ToBase64String(buffer.ToArray()),
            audioMimeType = "audio/wav"
        });
    }

    // GET /api/voicetest/greet
    // Called once when the browser page loads. Returns AVA's opening greeting
    // as base64 WAV audio so the user hears her voice immediately.
    //
    // WHY A DEDICATED ENDPOINT (not /chat)?
    // The greeting is fixed text — we don't need the full LLM pipeline.
    // Skipping Claude here cuts greeting latency to ~300ms (just TTS).
    // The /chat endpoint adds 800–1500ms for LLM inference on top of TTS.
    // For the very first thing the user hears, speed matters most.
    //
    // WHY NOT HARDCODE IN THE BROWSER?
    // The server synthesizes the greeting so it's in AVA's actual voice (JennyNeural).
    // A browser-side text message would be silent — no voice, no warmth.
    [HttpGet("greet")]
    public async Task<IActionResult> Greet(CancellationToken ct)
    {
        const string greetingText =
            "Hi there! I'm AVA, your emotional support companion. " +
            "I'm really glad you reached out today. " +
            "Before we begin, could you tell me your name? " +
            "And whenever you feel ready, I'm here to listen — " +
            "please share what's on your mind. " +
            "You're not alone.";

        _logger.LogInformation("Greet endpoint called — synthesizing opening message");

        var buffer = new MemoryStream();
        await foreach (var chunk in _tts.SynthesizeStreamAsync(greetingText, ct: ct))
            await buffer.WriteAsync(chunk, ct);

        return Ok(new
        {
            success       = true,
            greetingText  = greetingText,
            audioBase64   = Convert.ToBase64String(buffer.ToArray()),
            audioMimeType = "audio/wav"
        });
    }

    // GET /api/voicetest/ping
    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new { Message = "AVA Phase 3 is alive!", Timestamp = DateTime.UtcNow });
}

public record SynthesizeRequest(string Text, string Voice = "en-US-JennyNeural");
public record ChatRequest(string Text, Guid SessionId = default);
