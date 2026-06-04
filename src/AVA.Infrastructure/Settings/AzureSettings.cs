namespace AVA.Infrastructure.Settings;

// Strongly-typed settings bound from appsettings.json "Azure" section.
// Never hardcode API keys in code - always use the Options pattern.
// How it works:
//   1. appsettings.json has: "Azure": { "SpeechKey": "...", "SpeechRegion": "..." }
//   2. Program.cs registers: services.Configure<AzureSettings>(config.GetSection("Azure"))
//   3. Service receives:     IOptions<AzureSettings> in its constructor
//   4. Service uses:         opts.Value.SpeechKey
public class AzureSettings
{
    public const string SectionName = "Azure";
    public string SpeechKey { get; set; } = string.Empty;
    public string SpeechRegion { get; set; } = string.Empty;
}
