using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AVA.Infrastructure.Migrations;

// Initial migration — creates the ConversationSessions and ConversationMessages tables.
//
// WHY MIGRATIONS INSTEAD OF EnsureCreated()?
// EnsureCreated() creates the schema from the current model — but it can NEVER update
// an existing database. If you add a new column next month, EnsureCreated() silently
// ignores it on existing databases.
//
// Migrations track EVERY schema change as a versioned file. When the app starts,
// EF Core applies only the migrations that haven't run yet — safe incremental updates.
// This is how production databases are managed at every serious company.
//
// HOW TO ADD FUTURE MIGRATIONS (when schema changes):
//   dotnet ef migrations add <MigrationName> \
//     --project src/AVA.Infrastructure \
//     --startup-project src/AVA.Api
//
// HOW TO APPLY MIGRATIONS TO THE DATABASE:
//   dotnet ef database update \
//     --project src/AVA.Infrastructure \
//     --startup-project src/AVA.Api
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── ConversationSessions table ─────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "ConversationSessions",
            columns: table => new
            {
                Id           = table.Column<Guid>(type: "TEXT", nullable: false),
                UserName     = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "Friend"),
                UserGoals    = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false, defaultValue: ""),
                CreatedAt    = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                LastActiveAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConversationSessions", x => x.Id);
            });

        // ── ConversationMessages table ─────────────────────────────────────
        migrationBuilder.CreateTable(
            name: "ConversationMessages",
            columns: table => new
            {
                Id        = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role      = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Content   = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                Emotion   = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "Neutral"),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                table.ForeignKey(
                    name: "FK_ConversationMessages_ConversationSessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "ConversationSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // ── Indexes ────────────────────────────────────────────────────────
        // Index on SessionId alone — for COUNT queries and DELETE in TrimHistoryAsync
        migrationBuilder.CreateIndex(
            name: "IX_ConversationMessages_SessionId",
            table: "ConversationMessages",
            column: "SessionId");

        // Composite index on (SessionId, Id) — our most common query:
        // "get messages for session ordered by Id"
        migrationBuilder.CreateIndex(
            name: "IX_ConversationMessages_SessionId_Id",
            table: "ConversationMessages",
            columns: new[] { "SessionId", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Drop in reverse order (messages before sessions — FK constraint)
        migrationBuilder.DropTable(name: "ConversationMessages");
        migrationBuilder.DropTable(name: "ConversationSessions");
    }
}
