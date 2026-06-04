namespace AVA.Domain.Entities;

// One full conversation from start to end.
// ENCAPSULATION: _messages is private. Outside code can READ
// messages via Messages property but CANNOT add directly.
// They must go through AddMessage() which validates ownership.
// This prevents bugs where wrong messages get added.
public class Session
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid UserProfileId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; private set; }
    public bool IsActive => EndedAt is null;

    private readonly List<Message> _messages = [];
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();
    public int MessageCount => _messages.Count;

    public void AddMessage(Message message)
    {
        if (message.SessionId != Id)
            throw new InvalidOperationException(
                $"Message belongs to session {message.SessionId}, not {Id}");
        _messages.Add(message);
    }

    public void End()
    {
        if (!IsActive)
            throw new InvalidOperationException("Session is already ended.");
        EndedAt = DateTime.UtcNow;
    }
}
