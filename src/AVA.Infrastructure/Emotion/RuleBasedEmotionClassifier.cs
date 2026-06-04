using AVA.Application.Interfaces;
using AVA.Domain.Enums;

namespace AVA.Infrastructure.Emotion;

// Detects emotion from a text transcript using keyword matching.
//
// WHY RULE-BASED AND NOT AN ML MODEL?
// Options considered:
//   A) Azure Text Analytics sentiment API     - costs money, adds network latency
//   B) ML.NET local sentiment model           - complex to train, overkill for Phase 3
//   C) Ask Claude to classify (LLM-as-judge)  - doubles LLM call cost + latency
//   D) Keyword rules (THIS approach)          - instant, free, no network, good enough
//
// For an emotional support app, being directionally correct matters more than precision.
// If someone says "I'm feeling really down and hopeless" - we catch "down" and "hopeless"
// and route to Sad. That's enough for AVA to respond with appropriate empathy.
// We can swap this implementation later without changing ANY other file (interfaces FTW).
//
// HOW IT WORKS:
// 1. Lowercase the transcript
// 2. Check each emotion's keyword list against the transcript
// 3. Count matches per emotion (weighted by keyword severity)
// 4. Return the emotion with the highest score
// 5. Default to Neutral if no keywords match
public class RuleBasedEmotionClassifier : IEmotionAnalyzer
{
    // Keyword dictionary: each EmotionType maps to a list of trigger words/phrases.
    // Longer phrases (e.g., "can't go on") are matched as substrings too.
    // Words are ordered roughly from strongest signal to weakest.
    private static readonly Dictionary<EmotionType, string[]> _keywords = new()
    {
        [EmotionType.Sad] = new[]
        {
            "hopeless", "worthless", "empty", "depressed", "depression",
            "crying", "cry", "tears", "heartbroken", "devastated",
            "miserable", "unhappy", "sad", "down", "low", "blue",
            "grief", "grieving", "loss", "lost", "broken"
        },

        [EmotionType.Lonely] = new[]
        {
            "lonely", "alone", "isolated", "no one cares", "nobody cares",
            "no friends", "nobody", "no one", "left out", "excluded",
            "invisible", "forgotten", "abandoned", "by myself", "all alone",
            "no support", "disconnected"
        },

        [EmotionType.Anxious] = new[]
        {
            "anxious", "anxiety", "panic", "panicking", "overwhelmed",
            "scared", "terrified", "afraid", "fear", "worried", "worry",
            "nervous", "stress", "stressed", "can't breathe", "heart racing",
            "overthinking", "what if", "dread", "dreading"
        },

        [EmotionType.Angry] = new[]
        {
            "angry", "anger", "furious", "rage", "frustrated", "frustration",
            "irritated", "annoyed", "mad", "upset", "hate", "unfair",
            "betrayed", "lied", "cheated", "resentment", "resentful"
        },

        [EmotionType.Confused] = new[]
        {
            "confused", "confusing", "don't know", "don't understand",
            "lost", "unsure", "not sure", "uncertain", "what should i do",
            "no idea", "clueless", "stuck", "direction", "purpose"
        },

        [EmotionType.Motivated] = new[]
        {
            "motivated", "determined", "ready", "excited about",
            "goal", "goals", "achieve", "succeed", "success",
            "improve", "better myself", "work hard", "push through",
            "confident", "believe in myself", "can do this"
        },

        [EmotionType.Happy] = new[]
        {
            "happy", "great", "wonderful", "amazing", "fantastic",
            "good", "well", "fine", "okay", "okay now",
            "thankful", "grateful", "blessed", "joyful", "excited"
        }
    };

    // Analyzes the transcript and returns the most likely EmotionType.
    // Returns EmotionType.Neutral if no keywords match (calm, no strong signal).
    public EmotionType Analyze(string text)
    {
        // Guard: empty/null transcript = no emotion to detect
        if (string.IsNullOrWhiteSpace(text))
            return EmotionType.Neutral;

        var lower = text.ToLowerInvariant();

        // Count keyword hits for each emotion.
        // Using a simple count — each matched keyword scores 1 point.
        // Emotion with most matches wins.
        var scores = new Dictionary<EmotionType, int>();

        foreach (var (emotion, keywords) in _keywords)
        {
            var score = keywords.Count(keyword => lower.Contains(keyword));
            if (score > 0)
                scores[emotion] = score;
        }

        // No keywords matched → neutral baseline
        if (scores.Count == 0)
            return EmotionType.Neutral;

        // Return the emotion with the highest keyword match count
        // In case of a tie, OrderByDescending + First picks one deterministically
        return scores
            .OrderByDescending(kv => kv.Value)
            .First()
            .Key;
    }
}
