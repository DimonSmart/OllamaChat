using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public sealed class SessionWorkspaceAgentFileStoreTests
{
    [Fact]
    public async Task SwitchingWorkspace_UsesNextRootWithoutChangingExistingOperationRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ollama-chat-file-access-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(first, "first.txt"), "first", cancellationToken: TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(second, "second.txt"), "second", cancellationToken: TestContext.Current.CancellationToken);
            var store = new SessionWorkspaceAgentFileStore(first);

            Assert.Equal("first", await store.ReadAsync("first.txt", cancellationToken: TestContext.Current.CancellationToken));
            store.SetWorkspace(second);

            Assert.Null(await store.ReadAsync("first.txt", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("second", await store.ReadAsync("second.txt", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(Path.GetFullPath(second), store.WorkspacePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
