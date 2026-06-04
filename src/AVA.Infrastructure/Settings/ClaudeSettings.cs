namespace AVA.Infrastructure.Settings;

// Configuration for Claude LLM (Anthropic API).
//
// WHY A SEPARATE SETTINGS CLASS FOR CLAUDE?
// We already have AzureSettings for STT/TTS. Claude is a completely
// different vendor (Anthropic), so it gets its own settings class.
// This keeps concerns separated - if we ever swap Claude for GPT-4,
// we only change AnthropicLlmProvider and ClaudeSettings, nothing else.
//
// SETTINGS IN appsettings.json:
// "Claude": {
//   "ApiKey": "YOUR_ANTHROPIC_KEY_HERE",
//   "Model": "claude-sonnet-4-6",
//   "MaxTokens": 1024
// }
//
// WHY MaxTokens = 1024?
// AVA's responses should be conversational (2-4 sentences), not essays.
// 1024 tokens ≈ ~750 words — more than enough for a warm, focused reply.
// Limiting tokens also controls latency and cost.
public class ClaudeSettings
{
    public const string SectionName = "Claude";

    // Your Anthropic API key from https://console.anthropic.com
    // NEVER put the real key here - goes in appsettings.Development.json (gitignored)
    public string ApiKey { get; init; } = string.Empty;

    // Which Claude model to use.
    // claude-sonnet-4-6  = fast + smart (recommended for AVA - best latency/quality ratio)
    // claude-opus-4-6    = most intelligent (slower, more expensive)
    // claude-haiku-4-5-20251001  = fastest + cheapest (less nuanced emotional responses)
    public string Model { get; init; } = "claude-sonnet-4-6";

    // Maximum tokens Claude will generate per response.
    // 1024 tokens ≈ 750 words - enough for warm, conversational replies.
    public int MaxTokens { get; init; } = 1024;
}
