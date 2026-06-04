# AVA — Autonomous Voice Agent

> An emotionally intelligent voice companion built with C# .NET 8, Azure Cognitive Services, and Claude AI.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![C#](https://img.shields.io/badge/C%23-12.0-239120?logo=csharp)
![Azure](https://img.shields.io/badge/Azure-Cognitive%20Services-0078D4?logo=microsoftazure)
![Claude](https://img.shields.io/badge/Claude-claude--sonnet--4--6-D97757?logo=anthropic)
![SQLite](https://img.shields.io/badge/SQLite-EF%20Core-003B57?logo=sqlite)
![CI](https://github.com/AkhilMekala007/AVA-Voice-Agent/actions/workflows/ci.yml/badge.svg)

---

## What is AVA?

AVA is an emotional support voice agent designed for people who feel lonely, anxious, low in confidence, or uncertain about their path in life. It listens, understands how you feel, and responds like a caring and wise companion — in a real human voice.

**Key capabilities:**
- Accepts typed or spoken messages from the browser
- Detects the user's emotional state (sad, anxious, lonely, angry, motivated, etc.)
- Generates contextually aware, empathetic responses using Claude AI
- Speaks back in a natural neural voice (Azure Jenny Neural TTS)
- Remembers your conversation history across sessions using SQLite

---

## Architecture

AVA follows **Clean Architecture** with four layers. Inner layers never depend on outer layers.

```
┌─────────────────────────────────────────────┐
│  AVA.Api          — HTTP endpoints, SignalR  │
│  ┌───────────────────────────────────────┐  │
│  │  AVA.Infrastructure — Azure, Claude,  │  │
│  │                        EF Core/SQLite │  │
│  │  ┌─────────────────────────────────┐  │  │
│  │  │  AVA.Application — Interfaces,  │  │  │
│  │  │     DTOs, Pipeline Orchestrator │  │  │
│  │  │  ┌───────────────────────────┐  │  │  │
│  │  │  │  AVA.Domain — Entities,   │  │  │  │
│  │  │  │  Enums, zero dependencies │  │  │  │
│  │  │  └───────────────────────────┘  │  │  │
│  │  └─────────────────────────────────┘  │  │
│  └───────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

---

## Conversation Pipeline

Every message goes through this pipeline:

```
User speaks / types
       ↓
[Browser] WebM/Opus → 16kHz PCM WAV conversion
       ↓
POST /api/voicetest/transcribe  →  Azure STT  →  transcript text
       ↓
[Claude Haiku] Transcript refinement (removes filler words)
       ↓
[RuleBasedEmotionClassifier] keyword scan → EmotionType
       ↓
[EfCoreContextManager] Load last 20 messages from SQLite
       ↓
[DynamicSystemPromptBuilder] Build per-emotion system prompt
       ↓
[Claude claude-sonnet-4-6] Stream response tokens
       ↓
[EfCoreContextManager] Save exchange to SQLite
       ↓
[Azure Jenny Neural TTS] Text → WAV audio
       ↓
Browser plays audio + shows response text
```

---

## Project Structure

```
AVA/
├── src/
│   ├── AVA.Domain/               # Entities, Enums (zero dependencies)
│   ├── AVA.Application/          # Interfaces, DTOs, Orchestrator, Prompt Builder
│   ├── AVA.Infrastructure/       # Azure STT/TTS, Claude LLM, EF Core, SQLite
│   └── AVA.Api/                  # ASP.NET Core Web API, SignalR Hub, wwwroot UI
├── tests/
│   ├── AVA.UnitTests/            # L0 tests — isolated, all dependencies mocked
│   └── AVA.IntegrationTests/     # L1 tests — real pipeline with InMemory SQLite
└── .github/workflows/ci.yml      # GitHub Actions CI — runs on every push
```

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend language | C# 12 / .NET 8 |
| Web framework | ASP.NET Core Web API |
| Real-time | SignalR (WebSocket hub) |
| LLM | Anthropic Claude claude-sonnet-4-6 via Anthropic.SDK |
| Speech-to-Text | Azure Cognitive Services Speech SDK |
| Text-to-Speech | Azure Neural TTS (en-US-JennyNeural, WAV output) |
| Database | SQLite via EF Core 8 |
| ORM | Entity Framework Core 8 (Code-First, Fluent API) |
| Logging | Serilog (structured, console) |
| Unit Testing | xUnit + Moq + FluentAssertions |
| CI/CD | GitHub Actions |

---

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- An [Azure Cognitive Services](https://portal.azure.com) Speech resource (free tier works)
- An [Anthropic API key](https://console.anthropic.com)

### 1. Clone the repository

```bash
git clone https://github.com/AkhilMekala007/AVA-Voice-Agent.git
cd AVA-Voice-Agent
```

### 2. Add your API keys

Create `src/AVA.Api/appsettings.Development.json` (this file is gitignored — never committed):

```json
{
  "Azure": {
    "SpeechKey": "your-azure-speech-key",
    "SpeechRegion": "eastus"
  },
  "Claude": {
    "ApiKey": "your-anthropic-api-key"
  }
}
```

### 3. Run the API

```bash
dotnet run --project src/AVA.Api
```

Open your browser at `http://localhost:5163` — AVA will greet you immediately.

### 4. Run all tests

```bash
dotnet test
```

Expected output: **53 tests passing** (38 unit + 15 integration).

---

## API Endpoints

| Method | URL | Description |
|---|---|---|
| `GET` | `/api/voicetest/greet` | AVA's opening greeting (base64 WAV + text) |
| `POST` | `/api/voicetest/chat` | Full pipeline: text → Claude → TTS → JSON response |
| `POST` | `/api/voicetest/transcribe` | WAV upload → Azure STT → transcript |
| `POST` | `/api/voicetest/synthesize` | Text → Azure TTS → WAV audio file |
| `GET` | `/api/voicetest/ping` | Health check |
| `GET` | `/health` | Server status + current phase |
| `WS` | `/hub/voice` | SignalR WebSocket hub |

---

## Database

AVA uses SQLite for persistent conversation storage. The database is created automatically at first startup.

**Location:** `%LOCALAPPDATA%\AVA\ava_conversations.db`

**Schema:**

```
ConversationSessions          ConversationMessages
─────────────────────         ────────────────────────────
Id           (Guid, PK)       Id         (int, PK, auto)
UserName     (string)         SessionId  (Guid, FK)
UserGoals    (string)         Role       (User / Assistant)
CreatedAt    (DateTime)       Content    (string, max 10000)
LastActiveAt (DateTime)       Emotion    (string)
                              CreatedAt  (DateTime)
```

---

## Testing

| Suite | Count | What it tests |
|---|---|---|
| `AVA.UnitTests` | 38 | Individual classes in isolation with all dependencies mocked |
| `AVA.IntegrationTests` | 15 | Full pipeline with real EfCoreContextManager + InMemory SQLite + mocked Azure/Claude |

```bash
# Run all tests
dotnet test

# Run only unit tests
dotnet test tests/AVA.UnitTests

# Run only integration tests
dotnet test tests/AVA.IntegrationTests
```

---

## CI/CD

Every push to `main` or `develop` triggers the GitHub Actions pipeline:

1. **Restore** NuGet packages
2. **Build** the solution (`Release` configuration)
3. **Test** — all 53 tests must pass
4. **Upload** test results + code coverage as artifacts

No API keys are required in CI — all external services (Azure, Claude) are mocked in tests.

---

## Phases Completed

| Phase | Description | Status |
|---|---|---|
| 1 | Project scaffold, Clean Architecture, GitHub setup | ✅ Done |
| 2 | Azure STT + TTS pipeline, first unit tests | ✅ Done |
| 3 | Claude LLM integration, emotion detection, full conversation loop, browser UI | ✅ Done |
| 4 | SQLite persistence with EF Core, cross-session memory | ✅ Done |
| 5 | Integration tests, GitHub Actions CI, EF Core migrations | ✅ Done |

## Roadmap

- **Phase 6** — User authentication (email/password or OAuth) so AVA knows who you are without introductions
- **Phase 7** — Fix voice audio transcription (WebM → PCM conversion end-to-end)
- **Phase 8** — Azure deployment (App Service + Azure SQL)
- **Phase 9** — Mobile-friendly UI / React frontend

---

## Security

- Real API keys live only in `appsettings.Development.json` which is **gitignored**
- The `appsettings.json` committed to the repo contains only placeholder values
- GitHub push protection is enabled on this repository

---

## Author

**Akhil Mekala** — built as a full-stack backend project demonstrating Clean Architecture, Azure AI services integration, EF Core persistence, and production-grade testing practices.
