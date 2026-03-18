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

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Binding?.DisplayName))
            {
                return $"{ServerDescriptor.Name} / {Binding.DisplayName.Trim()}";
            }

            if (Binding?.BindingId is Guid bindingId && bindingId != Guid.Empty)
            {
                return $"{ServerDescriptor.Name} / {bindingId.ToString("N")[..8]}";
            }

            return ServerDescriptor.Name;
        }
    }
}
