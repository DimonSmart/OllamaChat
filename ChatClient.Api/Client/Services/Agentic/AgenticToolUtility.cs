using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class AgenticToolUtility
{
    public static string SerializeForToolTransport(object? value, JsonSerializerOptions jsonOptions)
    {
        try
        {
            return JsonSerializer.Serialize(value, jsonOptions);
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }
}
