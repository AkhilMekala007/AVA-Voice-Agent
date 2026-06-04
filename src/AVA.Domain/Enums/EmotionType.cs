namespace AVA.Domain.Enums;

// Every emotional state AVA can detect from user speech.
// Enum = compile-time safety. No typos, no silent bugs like "Anxxious".
public enum EmotionType
{
    Unknown,    // Default - not yet analyzed
    Neutral,    // No strong emotion
    Happy,      // Positive, upbeat
    Sad,        // Low, sorrowful
    Anxious,    // Worried, stressed, nervous
    Angry,      // Frustrated or furious
    Confused,   // Uncertain, lost
    Motivated,  // Energised, excited
    Lonely      // Isolated, no one to talk to
}
