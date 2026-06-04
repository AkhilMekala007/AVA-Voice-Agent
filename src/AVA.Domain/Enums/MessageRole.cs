namespace AVA.Domain.Enums;

// Who sent a message - the human or AVA.
// LLMs require every message tagged with a role so they know
// whose words are whose in the conversation history.
public enum MessageRole
{
    User,       // The human speaking to AVA
    Assistant   // AVA's response
}
