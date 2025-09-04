using System.Collections.Generic;
using System.Text.Json;

namespace ChatClient.Shared.Models;

public record McpFunctionCallRequest(string Server, string Function, Dictionary<string, JsonElement> Parameters);
