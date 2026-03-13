using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Common;

public static class PlanningJson
{
    public static JsonSerializerOptions CompactOptions { get; } = CreateOptions(writeIndented: false);

    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(writeIndented: true);

    public static string SerializeCompact<T>(T value) =>
        JsonSerializer.Serialize(value, CompactOptions);

    public static string SerializeIndented<T>(T value) =>
        JsonSerializer.Serialize(value, IndentedOptions);

    public static string SerializeElementCompact(JsonElement? value) =>
        value is null
            ? "null"
            : JsonSerializer.Serialize(value.Value, CompactOptions);

    public static string SerializeElementIndented(JsonElement? value) =>
        value is null
            ? "null"
            : JsonSerializer.Serialize(value.Value, IndentedOptions);

    public static string SerializeNodeIndented(JsonNode? value) =>
        value?.ToJsonString(IndentedOptions) ?? "null";

    private static JsonSerializerOptions CreateOptions(bool writeIndented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = writeIndented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
