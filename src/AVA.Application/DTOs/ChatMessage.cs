using AVA.Domain.Enums;

namespace AVA.Application.DTOs;

// Lightweight DTO sent to the LLM - just role + content.
// We do NOT send the full domain Message entity to Claude because
// it has DB IDs, timestamps, latency etc. that the LLM does not need.
// DTOs keep layers decoupled.
// Record = immutable by default. Perfect for data in transit.
public record ChatMessage(MessageRole Role, string Content);
