using ChatClient.Api.Services.BuiltIn;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class UserProfilePreferencesDocumentParserTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_LegacyFlatMap_MigratesKnownAndCustomValues()
    {
        const string json =
            """
            {
              "timezone": "Europe/Madrid",
              "clothingSize": "M",
              "preferredLanguage": "de"
            }
            """;

        var document = UserProfilePreferencesDocumentParser.Deserialize(
            json,
            JsonOptions,
            useDefaultWhenMissing: true);
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: true);

        Assert.True(snapshot.TryResolveKey("displayName", out var displayNameKey));
        Assert.Equal("displayName", displayNameKey);
        Assert.Equal("Europe/Madrid", snapshot.Values["timezone"]);
        Assert.Equal("M", snapshot.Values["clothingSize"]);
        Assert.Equal("de", snapshot.Values["preferredLanguage"]);
        Assert.Contains(document.Definitions, static definition =>
            string.Equals(definition.Key, "clothingSize", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            document.Definitions.Single(static definition => string.Equals(definition.Key, "preferredLanguage", StringComparison.OrdinalIgnoreCase)).AllowedValues,
            value => string.Equals(value, "de", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deserialize_EmptyObject_WithDefaultsRequested_ReturnsDefaultDocument()
    {
        var document = UserProfilePreferencesDocumentParser.Deserialize(
            "{}",
            JsonOptions,
            useDefaultWhenMissing: true);

        Assert.Contains(document.Definitions, static definition =>
            string.Equals(definition.Key, "displayName", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(UserProfilePreferencesDocument.DefaultServerDescription, document.ServerDescription);
    }

    [Fact]
    public void Deserialize_NewFormatDocument_PreservesExplicitSchema()
    {
        const string json =
            """
            {
              "serverDescription": "Provides clothing information for personalization.",
              "definitions": [
                {
                  "key": "clothingSize",
                  "description": "Current clothing size.",
                  "prompt": "What clothing size should I use?",
                  "allowedValues": [],
                  "aliases": []
                }
              ],
              "values": {
                "clothingSize": "L"
              }
            }
            """;

        var document = UserProfilePreferencesDocumentParser.Deserialize(
            json,
            JsonOptions,
            useDefaultWhenMissing: true);

        Assert.Equal("Provides clothing information for personalization.", document.ServerDescription);
        Assert.Single(document.Definitions);
        Assert.Equal("clothingSize", document.Definitions[0].Key);
        Assert.Equal("L", document.Values["clothingSize"]);
        Assert.DoesNotContain(document.Definitions, static definition =>
            string.Equals(definition.Key, "displayName", StringComparison.OrdinalIgnoreCase));
    }
}
