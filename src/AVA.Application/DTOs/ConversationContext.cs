namespace AVA.Application.DTOs;

// Everything AVA needs before it can respond:
//   - The last N messages (conversation history)
//   - Who it is talking to (UserName, Goals)
// Built by IContextManager and passed to ILlmProvider.
// IReadOnlyList = pipeline can read history but cannot modify it.
public record ConversationContext(
    Guid SessionId,
    IReadOnlyList<ChatMessage> Messages,
    string UserName = "Friend",
    string UserGoals = ""
);
