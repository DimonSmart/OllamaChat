using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class McpServerSessionContext(McpServerSessionBinding? binding)
{
    public McpServerSessionBinding? Binding { get; } = binding;
}
