using Microsoft.EntityFrameworkCore;

namespace AVA.Infrastructure.Persistence;

// The EF Core DbContext for AVA.
// Owns the database schema for conversation storage.
//
// WHY DbContext IN INFRASTRUCTURE (not Application)?
// DbContext has a hard dependency on EF Core — an external framework.
// The Application layer must stay framework-agnostic (Clean Architecture rule).
// Infrastructure is the correct home for all EF Core concerns.
//
// DESIGN DECISIONS:
// - We use Fluent API (OnModelCreating) instead of Data Annotations.
//   Fluent API keeps entity classes clean — no [Required], [MaxLength] etc.
//   on the entity files. One place (here) owns all schema rules.
//
// - Messages are stored with an explicit OrderBy(m => m.Id) contract.
//   This is documented in EfCoreContextManager so readers know the sort guarantee.
public class AvaDbContext : DbContext
{
    public DbSet<ConversationSession> Sessions { get; set; } = null!;
    public DbSet<ConversationMessage> Messages { get; set; } = null!;

    // Constructor accepting DbContextOptions — required for DI injection
    // and for passing InMemory options in unit tests.
    public AvaDbContext(DbContextOptions<AvaDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ── ConversationSession ────────────────────────────────────────────
        modelBuilder.Entity<ConversationSession>(e =>
        {
            e.HasKey(s => s.Id);

            e.Property(s => s.UserName)
                .HasMaxLength(100)
                .HasDefaultValue("Friend");

            e.Property(s => s.UserGoals)
                .HasMaxLength(1000)
                .HasDefaultValue(string.Empty);

            e.Property(s => s.CreatedAt)
                .HasDefaultValueSql("datetime('now')");   // SQLite syntax

            e.Property(s => s.LastActiveAt)
                .HasDefaultValueSql("datetime('now')");

            // One session has many messages; deleting a session cascades to its messages
            e.HasMany(s => s.Messages)
                .WithOne(m => m.Session)
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ConversationMessage ────────────────────────────────────────────
        modelBuilder.Entity<ConversationMessage>(e =>
        {
            e.HasKey(m => m.Id);

            e.Property(m => m.Role)
                .HasMaxLength(20)
                .IsRequired();

            e.Property(m => m.Content)
                .HasMaxLength(10000)
                .IsRequired();

            e.Property(m => m.Emotion)
                .HasMaxLength(50)
                .HasDefaultValue("Neutral");

            e.Property(m => m.CreatedAt)
                .HasDefaultValueSql("datetime('now')");

            // Index on SessionId — we always query by session, so this is critical for perf
            e.HasIndex(m => m.SessionId);

            // Composite index for "get messages for session, ordered" — our most common query
            e.HasIndex(m => new { m.SessionId, m.Id });
        });
    }
}
