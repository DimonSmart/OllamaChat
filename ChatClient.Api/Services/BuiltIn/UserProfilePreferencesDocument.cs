namespace ChatClient.Api.Services.BuiltIn;

public sealed class UserProfilePreferencesDocument
{
    internal const string DefaultServerDescription =
        "Provides configured user profile fields for personalization, such as display name, preferred language, timezone, tone, and other user-specific preferences.";

    public string ServerDescription { get; set; } = string.Empty;

    public List<UserProfilePreferenceDefinition> Definitions { get; set; } = [];

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static UserProfilePreferencesDocument CreateDefault() =>
        new()
        {
            ServerDescription = DefaultServerDescription,
            Definitions =
            [
                new()
                {
                    Key = "displayName",
                    Description = "Preferred user name used for personalized addressing.",
                    Prompt = "How should I address you?",
                    Aliases = ["name", "preferred_name", "preferredName", "userName"]
                },
                new()
                {
                    Key = "preferredLanguage",
                    Description = "Default answer language.",
                    Prompt = "Which language should I use by default when replying?",
                    DefaultValue = "ru",
                    AllowedValues = ["ru", "en", "es"]
                },
                new()
                {
                    Key = "tone",
                    Description = "Preferred communication tone.",
                    Prompt = "What communication tone do you prefer?",
                    DefaultValue = "neutral",
                    AllowedValues = ["neutral", "friendly", "formal"]
                },
                new()
                {
                    Key = "verbosity",
                    Description = "Preferred response detail level.",
                    Prompt = "How detailed should responses be?",
                    DefaultValue = "normal",
                    AllowedValues = ["short", "normal", "detailed"]
                },
                new()
                {
                    Key = "timezone",
                    Description = "Default time zone for time-related answers.",
                    Prompt = "Which time zone should be used for time-related information?",
                    DefaultValue = "Europe/Madrid"
                },
                new()
                {
                    Key = "measurementSystem",
                    Description = "Preferred measurement system.",
                    Prompt = "Which measurement system should be used?",
                    DefaultValue = "metric",
                    AllowedValues = ["metric", "imperial"]
                },
                new()
                {
                    Key = "grammarGenderRu",
                    Description = "Preferred grammatical gender forms for Russian.",
                    Prompt = "Which grammatical gender forms should be used in Russian?",
                    DefaultValue = "neutral",
                    AllowedValues = ["male", "female", "neutral"]
                },
                new()
                {
                    Key = "signature",
                    Description = "Optional signature for messages.",
                    Prompt = "What signature should be used in messages?"
                },
                new()
                {
                    Key = "devEnvironment",
                    Description = "Primary operating system.",
                    Prompt = "What operating system do you use?",
                    DefaultValue = "windows",
                    AllowedValues = ["windows", "macos", "linux", "other"]
                },
                new()
                {
                    Key = "editor",
                    Description = "Preferred IDE or editor.",
                    Prompt = "Which IDE or editor do you use?",
                    DefaultValue = "vscode",
                    AllowedValues = ["vs", "vscode", "rider", "other"]
                }
            ]
        };
}

public sealed class UserProfilePreferenceDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string? DefaultValue { get; set; }

    public List<string> AllowedValues { get; set; } = [];

    public List<string> Aliases { get; set; } = [];
}
