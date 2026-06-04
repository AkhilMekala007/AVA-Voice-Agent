using System.Runtime.CompilerServices;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using AVA.Application.DTOs;
using AVA.Application.Interfaces;
using AVA.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVA.Infrastructure.Llm;

// Implements ILlmProvider using the Anthropic Claude API.
//
// WHY ANTHROPIC CLAUDE AND NOT OPENAI GPT?
// 1. Claude excels at emotional intelligence and empathy in conversation.
// 2. Claude's Constitutional AI training makes it safer for mental health contexts.
// 3. Streaming token-by-token via IAsyncEnumerable<string> cuts perceived latency
//    because AVA starts speaking the first sentence before Claude finishes the full reply.
// 4. Anthropic.SDK is the official C# client — no HTTP boilerplate needed.
//
// HOW STREAMING WORKS:
// Instead of waiting for the full response (3-5 seconds), Claude sends tokens one-by-one:
//   "I " → "understand" → " you" → " feel" → " lonely" → "..."
// We yield each token as it arrives. The ConversationOrchestrator collects them,
// then passes the full text to TTS. This approach keeps the pipeline hot.
//
// WHY IAsyncEnumerable INSTEAD OF Task<string>?
// Task<string> = wait for ALL tokens, then return. Adds 2-4 seconds to latency.
// IAsyncEnumerable = yield each token as it arrives. Pipeline starts immediately.
public class AnthropicLlmProvider : ILlmProvider
{
    private readonly ClaudeSettings _settings;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    public AnthropicLlmProvider(
        IOptions<ClaudeSettings> settings,
        ILogger<AnthropicLlmProvider> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // Streams Claude's response token by token.
    // systemPrompt = AVA's personality instructions (built by DynamicSystemPromptBuilder)
    // history = all previous messages in this conversation (from InMemoryContextManager)
    //
    // [EnumeratorCancellation] tells the compiler to hook into the IAsyncEnumerable
    // cancellation machinery - if the user cancels (closes browser), Claude stops generating.
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        IReadOnlyList<ChatMessage> history,
        string systemPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Guard: if no messages, nothing to respond to
        if (history.Count == 0)
        {
            _logger.LogWarning("StreamCompletionAsync called with empty history - skipping LLM call");
            yield break;
        }

        // Convert our domain DTOs (ChatMessage) to Anthropic SDK's Message type
        // WHY CONVERT? Our domain layer must not reference Anthropic SDK types.
        // The interface uses ChatMessage (our DTO). Here in Infrastructure we translate.
        var anthropicMessages = history
            .Select(m => new Message
            {
                Role = m.Role == AVA.Domain.Enums.MessageRole.User
                    ? RoleType.User
                    : RoleType.Assistant,
                // SDK v2+: Content is List<ContentBase>, not a plain string
                Content = new List<ContentBase>
                {
                    new TextContent { Text = m.Content }
                }
            })
            .ToList();

        var request = new MessageParameters
        {
            Model = _settings.Model,
            MaxTokens = _settings.MaxTokens,
            // SDK v2+: System is List<SystemMessage>, not a plain string
            System = new List<SystemMessage>
            {
                new SystemMessage(systemPrompt)
            },
            Messages = anthropicMessages,
            Stream = true   // Enable server-sent events streaming
        };

        _logger.LogInformation(
            "Calling Claude {Model} with {MessageCount} messages",
            _settings.Model, history.Count);

        // StreamClaudeMessageAsync yields MessageStreamEvent objects as they arrive
        // Each event may contain a text delta (new token) or metadata (stop reason, usage)
        await foreach (var streamEvent in new AnthropicClient(_settings.ApiKey)
            .Messages
            .StreamClaudeMessageAsync(request, ct))
        {
            // We only care about text delta events - these carry actual response tokens
            if (streamEvent.Delta?.Text is { Length: > 0 } token)
            {
                yield return token;
            }
        }

        _logger.LogInformation("Claude streaming response complete");
    }
}
