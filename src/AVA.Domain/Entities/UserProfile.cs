namespace AVA.Domain.Entities;

// Stores what AVA knows about the user across sessions.
// Name, goals, preferences - this is what makes AVA feel
// like it actually remembers you.
public class UserProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Friend";
    public string CurrentEmotionState { get; set; } = "Neutral";
    public List<string> Goals { get; set; } = [];
    public Dictionary<string, string> Preferences { get; set; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
