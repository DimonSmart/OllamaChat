using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public sealed class SessionWorkspaceAgentFileStoreTests
{
    [Fact]
    public async Task CliStoreRejectsTraversalAbsolutePathsAndSymlinks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-file-boundary-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        try
        {
            var store = new SessionWorkspaceAgentFileStore(workspace, strictWorkspaceBoundary: true);
            await store.WriteAsync("valid.txt", "ok", TestContext.Current.CancellationToken);
            Assert.Equal("ok", await store.ReadAsync("valid.txt", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                store.WriteAsync("../outside/escape.txt", "bad", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                store.WriteAsync(Path.Combine(outside, "escape.txt"), "bad", TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => store.SetWorkspace(outside));
            var link = Path.Combine(workspace, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    store.WriteAsync("linked/escape.txt", "bad", TestContext.Current.CancellationToken));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Some Windows test runners do not allow creating symbolic links.
            }
            Assert.False(File.Exists(Path.Combine(outside, "escape.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
