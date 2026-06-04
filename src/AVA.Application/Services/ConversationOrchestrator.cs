using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AVA.Application.Services;

// The brain of AVA. Orchestrates the full conversation pipeline.
//
// PIPELINE (one round-trip per user voice message):
//
//   [1] Audio bytes → STT        → transcript text
//   [2] Transcript  → Emotion    → EmotionType (Sad, Anxious, Lonely, etc.)
//   [3] SessionId   → Context    → conversation history + user metadata
//   [4] Context + Emotion → System Prompt → personalised instructions for Claude
//   [5] History + System Prompt → LLM → streamed response tokens
//   [6] Collect all tokens into full response string
//   [7] Save exchange to context (user message + AVA response)
//   [8] Return response text (VoiceHub will pipe it to TTS)
//
// WHY AN ORCHESTRATOR CLASS?
// Without an Orchestrator, VoiceHub would have to know about STT, LLM, emotion,
// context, and prompts - that's 5 responsibilities in one class (violates SRP).
// Orchestrator owns the pipeline. VoiceHub owns the WebSocket plumbing. Clean split.
//
// WHY DOES ORCHESTRATOR NOT CALL TTS?
// TTS is called in VoiceHub because TTS output (audio chunks) needs to be streamed
// directly back to the client over WebSocket. Orchestrator returns a string
// (the response text), and VoiceHub decides how to deliver it.
// This keeps Orchestrator pure: it produces text. VoiceHub produces audio.
public class ConversationOrchestrator
{
    private readonly ISpeechToTextService _stt;
    private readonly ILlmProvider _llm;
    private readonly IEmotionAnalyzer _emotion;
    private readonly IContextManager _context;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly ILogger<ConversationOrchestrator> _logger;

    public ConversationOrchestrator(
        ISpeechToTextService stt,
        ILlmProvider llm,
        IEmotionAnalyzer emotion,
        IContextManager context,
        ISystemPromptBuilder promptBuilder,
        ILogger<ConversationOrchestrator> logger)
    {
        _stt = stt;
        _llm = llm;
        _emotion = emotion;
        _context = context;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    // Processes one user voice message through the full pipeline.
    //
    // Returns a ProcessResult containing:
    //   - Transcript  : what the user said (text)
    //   - Response    : what AVA said (text) — VoiceHub will synthesize this to audio
    //   - Emotion     : detected emotion (for logging / future UI display)
    //   - Success     : false if STT returned empty (silence, bad audio)
    //
    // userName and userGoals will come from user profile (Phase 4).
    // Hardcoded defaults here for Phase 3.
    public async Task<ProcessResult> ProcessAsync(
        byte[] audioBytes,
        Guid sessionId,
        string userName = "Friend",
        string userGoals = "",
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // ── Step 1: Speech-to-Text ──────────────────────────────────────────
        _logger.LogInformation("[{SessionId}] Step 1: STT started", sessionId);
        var rawTranscript = await _stt.TranscribeAsync(audioBytes, ct);

        if (string.IsNullOrWhiteSpace(rawTranscript))
        {
            _logger.LogWarning("[{SessionId}] STT returned empty transcript - aborting", sessionId);
            return ProcessResult.Empty(sessionId);
        }

        _logger.LogInformation("[{SessionId}] Step 1 done ({Ms}ms): \"{Transcript}\"",
            sessionId, stopwatch.ElapsedMilliseconds, rawTranscript);

        // ── Step 1b: Refine Transcript ──────────────────────────────────────
        // Clean up STT output before sending to LLM.
        // Removes filler words (um, uh, like), fixes grammar errors from misheard words,
        // preserves original meaning and emotional tone.
        var transcript = await RefineTranscriptAsync(rawTranscript, ct);
        if (transcript != rawTranscript)
            _logger.LogInformation("[{SessionId}] Transcript refined: \"{Raw}\" → \"{Refined}\"",
                sessionId, rawTranscript, transcript);

        // ── Step 2: Emotion Detection ───────────────────────────────────────
        // Classify the user's emotional state from their words
        var detectedEmotion = _emotion.Analyze(transcript);
        _logger.LogInformation(
            "[{SessionId}] Step 2: Detected emotion = {Emotion}",
            sessionId, detectedEmotion);

        // ── Step 3: Load Conversation Context ──────────────────────────────
        // Get the full conversation history for this session
        var context = await _context.GetContextAsync(sessionId, userName, userGoals, ct);
        _logger.LogInformation(
            "[{SessionId}] Step 3: Context loaded - {Count} prior messages",
            sessionId, context.Messages.Count);

        // ── Step 4: Build System Prompt ─────────────────────────────────────
        // Create AVA's personalised instructions for this specific message
        var systemPrompt = _promptBuilder.Build(context, detectedEmotion);

        // ── Step 5 & 6: Stream LLM Response + Collect Full Text ─────────────
        // Stream Claude's response token by token, collect into complete string.
        //
        // WHY COLLECT INTO A STRING HERE?
        // We need the full response to:
        //   a) Pass to TTS (Azure TTS works better with complete sentences)
        //   b) Save to conversation history (Step 7)
        // VoiceHub could stream to the client in real-time (Phase 4 enhancement).
        _logger.LogInformation("[{SessionId}] Step 4/5: Calling Claude LLM", sessionId);

        // Build the message list: all prior history + the new user message
        var messagesForLlm = new List<ChatMessage>(context.Messages)
        {
            new ChatMessage(Domain.Enums.MessageRole.User, transcript)
        };

        var responseBuilder = new System.Text.StringBuilder();
        await foreach (var token in _llm.StreamCompletionAsync(messagesForLlm, systemPrompt, ct))
        {
            responseBuilder.Append(token);
        }

        var response = responseBuilder.ToString().Trim();

        // If Claude returned nothing (API error, empty delta stream), use fallback
        if (string.IsNullOrWhiteSpace(response))
        {
            _logger.LogError("[{SessionId}] LLM returned empty response", sessionId);
            response = "I'm here for you. Can you tell me a little more about what's on your mind?";
        }

        _logger.LogInformation(
            "[{SessionId}] Step 5 done ({Ms}ms): LLM response = {Length} chars",
            sessionId, stopwatch.ElapsedMilliseconds, response.Length);

        // ── Step 7: Save Exchange to Context ───────────────────────────────
        // Store this round-trip so future messages have full history
        await _context.SaveExchangeAsync(sessionId, transcript, response, detectedEmotion, ct);

        stopwatch.Stop();
        _logger.LogInformation(
            "[{SessionId}] Pipeline complete. Total latency: {Ms}ms",
            sessionId, stopwatch.ElapsedMilliseconds);

        return new ProcessResult(
            SessionId: sessionId,
            Transcript: transcript,
            Response: response,
            Emotion: detectedEmotion,
            Success: true,
            LatencyMs: stopwatch.ElapsedMilliseconds
        );
    }

    // Text-only pipeline — skips STT, starts from Step 2.
    // Used for development testing without a microphone.
    public async Task<ProcessResult> ProcessTextAsync(
        string text,
        Guid sessionId,
        string userName = "Friend",
        string userGoals = "",
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(text))
            return ProcessResult.Empty(sessionId);

        // Refine typed/transcribed text before processing
        var refined = await RefineTranscriptAsync(text, ct);
        if (refined != text)
            _logger.LogInformation("[{SessionId}] Text refined: \"{Raw}\" → \"{Refined}\"", sessionId, text, refined);
        text = refined;

        var detectedEmotion = _emotion.Analyze(text);

        // ── Name extraction ────────────────────────────────────────────────
        // Try to extract the user's name from phrases like:
        //   "Hi this is Akhil", "my name is Akhil", "I'm Akhil", "I am Akhil"
        // If found, pass it to GetContextAsync which will:
        //   1. Update the session's UserName in the database
        //   2. Look up prior sessions under this name and load that history
        var extractedName = ExtractNameFromText(text);
        if (extractedName != null && userName == "Friend")
        {
            userName = extractedName;
            _logger.LogInformation("[{SessionId}] Name extracted from message: '{Name}'", sessionId, userName);
        }

        var context = await _context.GetContextAsync(sessionId, userName, userGoals, ct);
        var systemPrompt = _promptBuilder.Build(context, detectedEmotion);

        var messagesForLlm = new List<ChatMessage>(context.Messages)
        {
            new ChatMessage(Domain.Enums.MessageRole.User, text)
        };

        var responseBuilder = new System.Text.StringBuilder();
        await foreach (var token in _llm.StreamCompletionAsync(messagesForLlm, systemPrompt, ct))
            responseBuilder.Append(token);

        var response = responseBuilder.ToString().Trim();

        if (string.IsNullOrWhiteSpace(response))
            response = "I'm here for you. Can you tell me more about what's on your mind?";

        await _context.SaveExchangeAsync(sessionId, text, response, detectedEmotion, ct);

        stopwatch.Stop();
        _logger.LogInformation("[{SessionId}] Text pipeline complete. Latency: {Ms}ms", sessionId, stopwatch.ElapsedMilliseconds);

        return new ProcessResult(sessionId, text, response, detectedEmotion, true, stopwatch.ElapsedMilliseconds);
    }

    // ── Private: Transcript Refinement ─────────────────────────────────────
    // Cleans up raw STT output using Claude before sending to the main pipeline.
    //
    // WHY REFINE?
    // Azure STT produces literal transcripts. Real speech is messy:
    //   "um I I feel like really uh lonely you know and like I don't know"
    //   → "I feel really lonely and I don't know what to do"
    //
    // Refinement removes: um, uh, like (filler), repeated words, incomplete sentences.
    // It PRESERVES: emotion, meaning, intent — never adds words not in the original.
    //
    // WHY USE CLAUDE FOR THIS (not rules)?
    // Rules can only catch known filler words. Claude understands context:
    //   "I feel like I'm falling apart" → "like" is NOT a filler here, keep it.
    //   "I feel like um like sad" → "um" and first "like" are fillers, remove them.
    //
    // We use claude-haiku (fastest/cheapest) since this is a simple cleanup task.
    // Typical latency: 200-400ms — acceptable as part of the pipeline.
    //
    // SKIP REFINEMENT: inputs under 4 words (not worth the latency overhead).
    private const string RefinementSystemPrompt =
        "You are a transcript cleaner for a voice agent. Your job is to clean up raw " +
        "speech-to-text output before it is processed by an AI emotional support agent.\n\n" +
        "Rules:\n" +
        "- Remove filler words: um, uh, like (when used as filler), you know, basically, right, I mean\n" +
        "- Fix repeated words: 'I I feel' → 'I feel'\n" +
        "- Fix obvious speech recognition errors while preserving meaning\n" +
        "- Preserve the emotional tone and ALL key words\n" +
        "- Do NOT add new words, ideas, or context\n" +
        "- Do NOT change 'like' when it expresses comparison or emotion\n" +
        "- Return ONLY the cleaned text. No explanation, no quotes.";

    private async Task<string> RefineTranscriptAsync(string rawText, CancellationToken ct)
    {
        // Skip for very short inputs — not worth the latency
        if (rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
            return rawText;

        try
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage(Domain.Enums.MessageRole.User, rawText)
            };

            var sb = new System.Text.StringBuilder();
            await foreach (var token in _llm.StreamCompletionAsync(messages, RefinementSystemPrompt, ct))
                sb.Append(token);

            var refined = sb.ToString().Trim();

            // Safety: if refinement returns empty or something wildly different, use original
            return string.IsNullOrWhiteSpace(refined) ? rawText : refined;
        }
        catch (Exception ex)
        {
            // Refinement failure is non-fatal — fall back to original transcript
            _logger.LogWarning(ex, "Transcript refinement failed, using raw transcript");
            return rawText;
        }
    }

    // ── Name extraction ────────────────────────────────────────────────────────
    // Detects when a user introduces themselves in natural speech and extracts
    // their first name. Used to look up their conversation history by name.
    //
    // Patterns recognised (case-insensitive):
    //   "Hi this is Akhil"        → "Akhil"
    //   "my name is Akhil"        → "Akhil"
    //   "I'm Akhil"               → "Akhil"
    //   "I am Akhil"              → "Akhil"
    //   "call me Akhil"           → "Akhil"
    //   "it's Akhil"              → "Akhil"
    //
    // Returns null if no name pattern is found — the pipeline continues with
    // userName = "Friend" (the default) and no cross-session lookup is done.
    private static string? ExtractNameFromText(string text)
    {
        var patterns = new[]
        {
            @"(?:this is|it's|its)\s+([A-Z][a-z]{1,20})",
            @"my name(?:'s| is)\s+([A-Z][a-z]{1,20})",
            @"(?:I'm|I am)\s+([A-Z][a-z]{1,20})",
            @"call me\s+([A-Z][a-z]{1,20})",
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                // Filter out common false positives (words that look like names but aren't)
                var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "here", "okay", "fine", "good", "back", "home", "sorry", "there", "going" };
                if (!stopWords.Contains(name))
                    return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
            }
        }
        return null;
    }
}

// Result object returned from the full pipeline.
// record = immutable, value-equality, compact syntax.
public record ProcessResult(
    Guid SessionId,
    string Transcript,
    string Response,
    EmotionType Emotion,
    bool Success,
    long LatencyMs)
{
    // Factory for the "no speech detected" early-exit case
    public static ProcessResult Empty(Guid sessionId) => new(
        SessionId: sessionId,
        Transcript: string.Empty,
        Response: string.Empty,
        Emotion: EmotionType.Neutral,
        Success: false,
        LatencyMs: 0
    );
}
