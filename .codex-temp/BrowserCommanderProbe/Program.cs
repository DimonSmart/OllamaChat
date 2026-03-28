using System.Text.Json;
using ModelContextProtocol.Client;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: BrowserCommanderProbe <tool-name> [json-arguments]");
    return 1;
}

var toolName = args[0];
var argumentsJson = args.Length > 1 ? args[1] : "{}";
Dictionary<string, object?> toolArguments;

try
{
    toolArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
        argumentsJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? [];
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Invalid JSON arguments: {ex.Message}");
    return 2;
}

await using var client = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "browsercommander-probe",
        Endpoint = new Uri("http://localhost:5082/mcp")
    }));

var result = await client.CallToolAsync(toolName, toolArguments);
var output = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
});

Console.WriteLine(output);
return 0;
