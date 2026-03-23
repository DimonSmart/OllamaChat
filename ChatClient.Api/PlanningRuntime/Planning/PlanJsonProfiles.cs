using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanJsonProfiles
{
    public static JsonSerializerOptions DraftCompactOptions { get; } = CreateOptions(PlanModelProfile.Draft, writeIndented: false);

    public static JsonSerializerOptions DraftIndentedOptions { get; } = CreateOptions(PlanModelProfile.Draft, writeIndented: true);

    public static JsonSerializerOptions RuntimeCompactOptions { get; } = CreateOptions(PlanModelProfile.Runtime, writeIndented: false);

    public static JsonSerializerOptions RuntimeIndentedOptions { get; } = CreateOptions(PlanModelProfile.Runtime, writeIndented: true);

    public static JsonSerializerOptions GetOptions(PlanModelProfile profile, bool writeIndented = false) =>
        (profile, writeIndented) switch
        {
            (PlanModelProfile.Draft, true) => DraftIndentedOptions,
            (PlanModelProfile.Draft, false) => DraftCompactOptions,
            (PlanModelProfile.Runtime, true) => RuntimeIndentedOptions,
            _ => RuntimeCompactOptions
        };

    public static string SerializeCompact<T>(T value, PlanModelProfile profile) =>
        JsonSerializer.Serialize(value, GetOptions(profile));

    public static string SerializeIndented<T>(T value, PlanModelProfile profile) =>
        JsonSerializer.Serialize(value, GetOptions(profile, writeIndented: true));

    public static JsonNode? SerializeToNode<T>(T value, PlanModelProfile profile) =>
        JsonSerializer.SerializeToNode(value, GetOptions(profile));

    public static JsonElement SerializeToElement<T>(T value, PlanModelProfile profile) =>
        JsonSerializer.SerializeToElement(value, GetOptions(profile));

    [return: MaybeNull]
    public static T Deserialize<T>(JsonNode? value, PlanModelProfile profile)
    {
        if (value is null)
            return default;

        return value.Deserialize<T>(GetOptions(profile))!;
    }

    private static JsonSerializerOptions CreateOptions(PlanModelProfile profile, bool writeIndented)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (profile == PlanModelProfile.Draft)
            resolver.Modifiers.Add(RemoveRuntimeStepProperties);

        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = resolver
        };
    }

    private static void RemoveRuntimeStepProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(PlanStep))
            return;

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            var property = typeInfo.Properties[index];
            if (property.Name is "s" or "res" or "err")
                typeInfo.Properties.RemoveAt(index);
        }
    }
}
