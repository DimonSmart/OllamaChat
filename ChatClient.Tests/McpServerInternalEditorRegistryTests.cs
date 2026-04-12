using ChatClient.Api.Client.Components;
using ChatClient.Api.Client.Services;
using ChatClient.Api.Services.BuiltIn;

namespace ChatClient.Tests;

public class McpServerInternalEditorRegistryTests
{
    [Fact]
    public void TryGetEditor_UserProfilePrefsServer_ReturnsRegisteredEditor()
    {
        var registry = new McpServerInternalEditorRegistry();

        var found = registry.TryGetEditor(BuiltInUserProfilePrefsServerTools.Descriptor, out var registration);

        Assert.True(found);
        Assert.NotNull(registration);
        Assert.Equal("User Profile Preferences", registration.Title);
        Assert.Equal(typeof(UserProfilePrefsInternalEditor), registration.ComponentType);
    }

    [Fact]
    public void TryGetEditor_BuiltInServerWithoutEditor_ReturnsFalse()
    {
        var registry = new McpServerInternalEditorRegistry();

        var found = registry.TryGetEditor(BuiltInWebMcpServerTools.Descriptor, out var registration);

        Assert.False(found);
        Assert.Null(registration);
    }
}
