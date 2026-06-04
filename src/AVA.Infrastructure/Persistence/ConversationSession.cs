namespace AVA.Infrastructure.Persistence;

// EF Core entity — represents one user session with AVA.
//
// WHY A SEPARATE ENTITY (not just use Domain's Session)?
// Domain entities are pure business logic — they must not have EF Core
// attributes or navigation properties (that would couple Domain to Infrastructure).
// ConversationSession is the Infrastructure's own storage model.
// The mapper in EfCoreContextManager translates between the two.
//
// COLUMNS:
//   Id            — matches the Guid sessionId from the browser (crypto.randomUUID)
//   UserName      — captured from first message ("Hi I'm Alex")
//   UserGoals     — what the user wants to achieve ("I want to feel more confident")
//   CreatedAt     — when the session started
//   LastActiveAt  — updated every round-trip (used for session expiry in Phase 5)
//   Messages      — navigation property → all messages for this session
public class ConversationSession
{
    public Guid   Id           { get; set; }
    public string UserName     { get; set; } = "Friend";
    public string UserGoals    { get; set; } = string.Empty;
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    // Navigation property — EF Core loads these when Include() is called
    public ICollection<ConversationMessage> Messages { get; set; }
        = new List<ConversationMessage>();
}
