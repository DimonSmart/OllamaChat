using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ChatClient.Api.Services.BuiltIn;

public interface IBuiltInMcpServerDescriptor : IMcpServerDescriptor
{
    string Key { get; }
    void RegisterTools(IMcpServerBuilder builder);
}

public sealed class BuiltInMcpServerDescriptor(
    Guid id,
    string key,
    string name,
    string description,
    Action<IMcpServerBuilder> registerTools) : IBuiltInMcpServerDescriptor
{
    private readonly Action<IMcpServerBuilder> _registerTools =
        registerTools ?? throw new ArgumentNullException(nameof(registerTools));

    public Guid? Id { get; } = id;
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
    public string Description { get; } = description ?? string.Empty;

    public void RegisterTools(IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _registerTools(builder);
    }
}
