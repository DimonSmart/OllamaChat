using System.Collections.Generic;

namespace ChatClient.Shared.Models;

public record McpFunctionCallRequest(string Server, string Function, Dictionary<string, string> Parameters);
