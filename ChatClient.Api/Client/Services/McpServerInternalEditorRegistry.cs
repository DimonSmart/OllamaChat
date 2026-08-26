using ChatClient.Api.Client.Components;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services;

public interface IMcpServerInternalEditorRegistry
{
    bool TryGetEditor(IMcpServerDescriptor serverDescriptor, out McpServerInternalEditorRegistration? registration);
}

public sealed record McpServerInternalEditorRegistration(
    string Title,
    Type ComponentType);

public sealed class McpServerInternalEditorRegistry : IMcpServerInternalEditorRegistry
{
    private static readonly IReadOnlyDictionary<string, McpServerInternalEditorRegistration> Registrations =
        new Dictionary<string, McpServerInternalEditorRegistration>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInUserMemoryMcpServerTools.Descriptor.Key] = new(
                Title: "User Memory Preferences",
                ComponentType: typeof(UserProfilePrefsInternalEditor))
        };

    public bool TryGetEditor(IMcpServerDescriptor serverDescriptor, out McpServerInternalEditorRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        if (serverDescriptor is IBuiltInMcpServerDescriptor builtIn &&
            Registrations.TryGetValue(builtIn.Key, out var builtInRegistration))
        {
            registration = builtInRegistration;
            return true;
        }

        registration = null;
        return false;
    }
}
