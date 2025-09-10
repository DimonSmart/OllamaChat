using System.Collections.Generic;
using System.Text.Json;

namespace ChatClient.Domain.Models;

public record McpFunctionCallRequest(string Server, string Function, Dictionary<string, JsonElement> Parameters);
