using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Shared;

public static class PlanningNodeJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static JsonNode? CloneNode(JsonNode? value) =>
        value?.DeepClone();

    public static JsonNode? ToNode(object? value) =>
        value is null
            ? null
            : JsonSerializer.SerializeToNode(value, SerializerOptions);

    public static JsonNode? ToNode(JsonElement value) =>
        JsonNode.Parse(value.GetRawText());

    public static JsonElement ToElement(JsonNode? value)
    {
        if (value is null)
        {
            using var document = JsonDocument.Parse("null");
            return document.RootElement.Clone();
        }

        using var documentWithValue = JsonDocument.Parse(value.ToJsonString());
        return documentWithValue.RootElement.Clone();
    }

    public static T DeserializeNode<T>(JsonNode? node) =>
        node is null
            ? throw new InvalidOperationException($"Could not deserialize JSON node to {typeof(T).Name}.")
            : node.Deserialize<T>(SerializerOptions)
              ?? throw new InvalidOperationException($"Could not deserialize JSON node to {typeof(T).Name}.");

    public static string SerializeIndented<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);
}
