using AVA.Application.Interfaces;
using AVA.Application.Services;
using AVA.Api.Hubs;
using AVA.Infrastructure.Context;
using AVA.Infrastructure.Emotion;
using AVA.Infrastructure.Llm;
using AVA.Infrastructure.Persistence;
using AVA.Infrastructure.Settings;
using AVA.Infrastructure.Stt;
using AVA.Infrastructure.Tts;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ─────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .MinimumLevel.Debug()
    .CreateLogger();
builder.Host.UseSerilog();

// ── Configuration ────────────────────────────────────────────────────────────
builder.Services.Configure<AzureSettings>(
    builder.Configuration.GetSection(AzureSettings.SectionName));

builder.Services.Configure<ClaudeSettings>(
    builder.Configuration.GetSection(ClaudeSettings.SectionName));

// ── Phase 4: SQLite + EF Core ─────────────────────────────────────────────────
// WHY AddDbContextFactory (not AddDbContext)?
// EfCoreContextManager is registered as Singleton (must live for the app's lifetime).
// A normal DbContext is Scoped (one per HTTP request). Injecting Scoped into Singleton
// causes a "captive dependency" — EF Core would throw at runtime.
// AddDbContextFactory creates a factory that's safe to inject into Singletons.
// Each operation calls _factory.CreateDbContextAsync() for a fresh, short-lived context.
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AVA", "ava_conversations.db");

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContextFactory<AvaDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           .EnableSensitiveDataLogging(builder.Environment.IsDevelopment())
           .EnableDetailedErrors(builder.Environment.IsDevelopment()));

Log.Information("SQLite database path: {DbPath}", dbPath);

// ── Infrastructure Services ───────────────────────────────────────────────────
builder.Services.AddSingleton<ISpeechToTextService, AzureSpeechToTextService>();
builder.Services.AddSingleton<ITextToSpeechService, AzureTextToSpeechService>();
builder.Services.AddSingleton<ILlmProvider, AnthropicLlmProvider>();
builder.Services.AddSingleton<IEmotionAnalyzer, RuleBasedEmotionClassifier>();

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<ISystemPromptBuilder, DynamicSystemPromptBuilder>();

// Phase 4: EfCoreContextManager replaces InMemoryContextManager.
// Conversations now persist across server restarts.
builder.Services.AddSingleton<IContextManager, EfCoreContextManager>();

builder.Services.AddSingleton<ConversationOrchestrator>();

// ── Web Framework Services ────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "AVA Voice Agent API", Version = "v1" }));

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5163", "https://localhost:5001")
     .AllowAnyMethod()
     .AllowAnyHeader()
     .AllowCredentials()));

var app = builder.Build();

// ── Apply database migrations on startup ──────────────────────────────────────
// MigrateAsync() applies any pending migrations to bring the database schema
// up to date. Safe to call on every startup — it's a no-op if already current.
//
// WHY MigrateAsync() instead of EnsureCreated()?
// EnsureCreated() creates the schema once but can NEVER update an existing DB.
// MigrateAsync() applies versioned migration files incrementally —
// safe for production because existing data is never lost during upgrades.
//
// The migration files live in: src/AVA.Infrastructure/Migrations/
// To add a new migration after changing entities:
//   dotnet ef migrations add <Name> --project src/AVA.Infrastructure --startup-project src/AVA.Api
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AvaDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // MigrateAsync() is relational-only — it fails on the InMemory provider used in tests.
    // Check the provider first: use MigrateAsync for real SQLite, EnsureCreated for InMemory.
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
        Log.Information("Database migrations applied at startup");
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
        Log.Information("InMemory database schema created at startup (test environment)");
    }
}

// ── Middleware Pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapHub<VoiceHub>("/hub/voice");

app.MapGet("/health", () => Results.Ok(new
{
    Status    = "Healthy",
    Timestamp = DateTime.UtcNow,
    Phase     = "4 - Persistent Conversations (SQLite)"
}));

Log.Information("AVA Voice Agent starting - Phase 4 (Persistent Conversation Storage)");
app.Run();

// Needed for WebApplicationFactory in integration tests.
// WebApplicationFactory<Program> requires Program to be a visible type.
// Top-level statement programs generate an implicit Program class —
// this partial declaration makes it accessible from the test project.
public partial class Program { }
