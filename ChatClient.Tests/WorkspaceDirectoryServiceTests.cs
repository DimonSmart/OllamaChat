using ChatClient.Api.Services;

namespace ChatClient.Tests;

public class WorkspaceDirectoryServiceTests
{
    private readonly WorkspaceDirectoryService _service = new();

    [Fact]
    public void GetDirectories_ReturnsDirectoriesButNotFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            File.WriteAllText(Path.Combine(root, "file.txt"), "content");

            Assert.Equal([directory], _service.GetDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../workspace")]
    [InlineData("workspace/child")]
    [InlineData("workspace\\child")]
    public void IsValidWorkspaceName_RejectsUnsafeValues(string name) =>
        Assert.False(_service.IsValidWorkspaceName(name));

    [Fact]
    public void CreateWorkspace_CreatesDirectChildAndReusesExistingDirectory()
    {
        var container = CreateTempDirectory();
        var root = Path.Combine(container, "workspaces");
        try
        {
            var created = _service.CreateWorkspace(root, "my-project");
            var reused = _service.CreateWorkspace(root, "my-project");

            Assert.Equal(Path.GetFullPath(Path.Combine(root, "my-project")), created);
            Assert.Equal(created, reused);
            Assert.True(Directory.Exists(created));
        }
        finally { Directory.Delete(container, true); }
    }

    [Fact]
    public void CreateWorkspace_RejectsAbsolutePathsAndCannotEscapeRoot()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Throws<ArgumentException>(() => _service.CreateWorkspace(root, Path.GetTempPath()));
            Assert.Throws<ArgumentException>(() => _service.CreateWorkspace(root, ".."));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GetParent_ReturnsNullForFileSystemRoot()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Null(_service.GetParent(root));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
