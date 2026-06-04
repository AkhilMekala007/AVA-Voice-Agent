namespace AVA.Infrastructure.Persistence;

// EF Core entity — one message in a conversation (either from user or AVA).
//
// WHY int Id (not Guid)?
// Messages are ordered by insertion. An auto-increment int Id gives us
// natural ordering for free — no need for a separate OrderIndex column.
// Guid IDs don't sort chronologically without an extra timestamp column.
//
// COLUMNS:
//   Id        — auto-increment PK, used for ordering messages
//   SessionId — FK to ConversationSession
//   Role      — "user" or "assistant" (matches Claude API message roles)
//   Content   — the actual text of the message
//   Emotion   — emotion detected at the time of this message (e.g. "Sad", "Anxious")
//               Stored as string so we don't need EF migrations when we add new EmotionTypes
//   CreatedAt — UTC timestamp, backup ordering if Id ever gaps
public class ConversationMessage
{
    public int    Id        { get; set; }
    public Guid   SessionId { get; set; }
    public string Role      { get; set; } = string.Empty;  // "user" | "assistant"
    public string Content   { get; set; } = string.Empty;
    public string Emotion   { get; set; } = "Neutral";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property — back reference to the owning session
    public ConversationSession Session { get; set; } = null!;
}
