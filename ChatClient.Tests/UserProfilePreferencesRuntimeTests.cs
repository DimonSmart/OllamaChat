using ChatClient.Api.Services.BuiltIn;

namespace ChatClient.Tests;

public class UserProfilePreferencesRuntimeTests
{
    [Fact]
    public void CreateSnapshot_NormalizesValuesAndDropsUndefinedKeys()
    {
        var document = new UserProfilePreferencesDocument
        {
            Definitions =
            [
                new()
                {
                    Key = "displayName",
                    Description = "Preferred user name.",
                    Prompt = "How should I address you?",
                    Aliases = ["name"]
                },
                new()
                {
                    Key = "tone",
                    Description = "Preferred tone.",
                    Prompt = "What tone do you prefer?",
                    AllowedValues = ["neutral", "formal"]
                }
            ],
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "  Alice  ",
                ["tone"] = "FORMAL",
                ["unknown"] = "should be removed"
            }
        };

        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: false);

        Assert.Equal("Alice", snapshot.Values["displayName"]);
        Assert.Equal("formal", snapshot.Values["tone"]);
        Assert.False(snapshot.Values.ContainsKey("unknown"));
        Assert.Contains(snapshot.AcceptedKeys, value => string.Equals(value, "name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildServerDescription_WithCustomDescription_ReturnsItVerbatim()
    {
        var document = new UserProfilePreferencesDocument
        {
            ServerDescription = "Provides current user clothing size, preferred language, and timezone.",
            Definitions =
            [
                new()
                {
                    Key = "clothingSize",
                    Description = "Current clothing size.",
                    Prompt = "What clothing size should be used?"
                }
            ]
        };

        var description = UserProfilePreferencesRuntime.BuildServerDescription(document);

        Assert.Equal(document.ServerDescription, description);
    }

    [Fact]
    public void BuildPrefsGetDescription_DefaultProfile_EmphasizesCurrentUserName()
    {
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(
            UserProfilePreferencesDocument.CreateDefault(),
            useDefaultWhenMissing: true);

        var description = UserProfilePreferencesRuntime.BuildPrefsGetDescription(snapshot);

        Assert.Contains("current user's display name", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred user name", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("greeting the user", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("displayName", description, StringComparison.OrdinalIgnoreCase);
    }
}
