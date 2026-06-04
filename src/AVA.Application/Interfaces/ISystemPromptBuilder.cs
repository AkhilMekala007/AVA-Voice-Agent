using AVA.Application.DTOs;
using AVA.Domain.Enums;

namespace AVA.Application.Interfaces;

// Builds AVA's system prompt dynamically for each turn.
// The prompt changes based on detected emotion:
//   Sad    -> "be extra gentle and validating"
//   Anxious -> "be calm and grounding"
//   Motivated -> "match their energy!"
// This is what makes AVA feel human, not robotic.
// Implementations: DynamicSystemPromptBuilder
public interface ISystemPromptBuilder
{
    string Build(ConversationContext context, EmotionType currentEmotion);
}
