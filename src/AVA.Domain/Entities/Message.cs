using AVA.Domain.Enums;

namespace AVA.Domain.Entities;

// One turn in a conversation - either user speech or AVA reply.
// LatencyMs stored so we can track AVA's performance per response.
// DetectedEmotion stored so we can see how user mood changes over time.
public class Message
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public EmotionType DetectedEmotion { get; init; } = EmotionType.Unknown;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public double LatencyMs { get; init; }
}
