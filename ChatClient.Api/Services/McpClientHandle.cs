using ChatClient.Domain.Models;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public sealed record McpClientHandle(
    McpClient Client,
    IMcpServerDescriptor ServerDescriptor,
    McpServerSessionBinding? Binding)
{
    public string BaseServerName => ServerDescriptor.Name;

    public Guid? BindingId => Binding?.BindingId;

    public string? BindingDisplayName => McpBindingPresentation.GetBindingLabel(Binding);

    public string DisplayName => McpBindingPresentation.GetServerDisplayName(ServerDescriptor, Binding);
}
