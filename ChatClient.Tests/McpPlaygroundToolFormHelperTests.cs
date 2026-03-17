using System.Text.Json;
using ChatClient.Api.Client.Pages;

namespace ChatClient.Tests;

public class McpPlaygroundToolFormHelperTests
{
    [Fact]
    public void CreateFields_ResolvesNullableAndCompositeSchemaTypes()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": ["array", "null"],
                  "items": { "type": "string" },
                  "description": "Optional path."
                },
                "contentMarkdown": {
                  "anyOf": [
                    { "type": "string" },
                    { "type": "null" }
                  ],
                  "description": "Optional markdown."
                },
                "mergeMode": {
                  "type": "string",
                  "description": "Merge behavior."
                }
              },
              "required": ["mergeMode"]
            }
            """);

        var fields = McpPlaygroundToolFormHelper.CreateFields(document.RootElement);

        Assert.Collection(
            fields,
            path =>
            {
                Assert.Equal("path", path.Name);
                Assert.Equal("array", path.Type);
                Assert.False(path.IsRequired);
            },
            content =>
            {
                Assert.Equal("contentMarkdown", content.Name);
                Assert.Equal("string", content.Type);
                Assert.False(content.IsRequired);
            },
            mergeMode =>
            {
                Assert.Equal("mergeMode", mergeMode.Name);
                Assert.Equal("string", mergeMode.Type);
                Assert.True(mergeMode.IsRequired);
            });
    }

    [Fact]
    public void BuildArguments_OmitsEmptyOptionalValues_AndParsesJsonInputs()
    {
        var fields = new[]
        {
            new McpPlaygroundToolFormHelper.FieldDefinition("path", "array", "Optional path.", false),
            new McpPlaygroundToolFormHelper.FieldDefinition("maxDepth", "integer", "Depth.", false),
            new McpPlaygroundToolFormHelper.FieldDefinition("mergeMode", "string", "Mode.", true),
            new McpPlaygroundToolFormHelper.FieldDefinition("contentMarkdown", "string", "Content.", false)
        };

        Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = "[\"Cooking\",\"Pastry\"]",
            ["maxDepth"] = null,
            ["mergeMode"] = "replace",
            ["contentMarkdown"] = "   "
        };

        var args = McpPlaygroundToolFormHelper.BuildArguments(fields, parameters);

        Assert.Equal(2, args.Count);
        Assert.Equal("replace", Assert.IsType<string>(args["mergeMode"]));

        var path = Assert.IsType<JsonElement>(args["path"]);
        Assert.Equal(JsonValueKind.Array, path.ValueKind);
        Assert.Equal(
            ["Cooking", "Pastry"],
            path.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void BuildArguments_InvalidArrayInput_ThrowsHelpfulMessage()
    {
        var fields = new[]
        {
            new McpPlaygroundToolFormHelper.FieldDefinition("titlePath", "array", "Title path.", false)
        };

        Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["titlePath"] = "Readme"
        };

        var ex = Assert.Throws<McpPlaygroundToolFormHelper.InvalidPlaygroundInputException>(
            () => McpPlaygroundToolFormHelper.BuildArguments(fields, parameters));

        Assert.Equal("titlePath", ex.FieldName);
        Assert.Contains("expects a JSON array", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[\"Readme\"]", ex.Message, StringComparison.Ordinal);
    }
}
