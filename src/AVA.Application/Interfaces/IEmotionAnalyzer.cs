using AVA.Domain.Enums;

namespace AVA.Application.Interfaces;

// WHY NOT USE THE LLM FOR EMOTION DETECTION?
// Calling Claude for emotion = 300ms extra latency + token cost per message.
// A keyword classifier runs in under 5ms and costs zero.
// Good enough for MVP. Can swap to LLM-based later - same interface, no other changes.
//
// Implementations: RuleBasedEmotionClassifier
public interface IEmotionAnalyzer
{
    EmotionType Analyze(string text);
}
