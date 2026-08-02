using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.Sandbox;

namespace ChatClient.Tests;

public sealed class SessionToolApprovalPolicyTests
{
    [Fact]
    public void Grant_ScopesShellFileAccessAndOrdinaryTools()
    {
        var policy = new SessionToolApprovalPolicy();
        policy.SetWorkspace(Path.Combine(Path.GetTempPath(), "approval-workspace"));

        policy.Grant(SandboxToolNames.RunShell);
        policy.Grant("file_access_read");
        policy.Grant("ordinary_tool");

        Assert.True(policy.IsApproved(SandboxToolNames.RunShell));
        Assert.True(policy.IsApproved("file_access_write"));
        Assert.False(policy.IsApproved("file_access_custom"));
        Assert.True(policy.IsApproved("ordinary_tool"));
        Assert.False(policy.IsApproved("another_tool"));
    }

    [Fact]
    public void SetWorkspace_ClearsOnlyFileAccessGrantAndNormalizesTrailingSeparator()
    {
        var policy = new SessionToolApprovalPolicy();
        var workspace = Path.Combine(Path.GetTempPath(), "approval-workspace");
        policy.SetWorkspace(workspace + Path.DirectorySeparatorChar);
        policy.Grant("file_access_read");
        policy.Grant(SandboxToolNames.RunShell);
        policy.Grant("ordinary_tool");

        policy.SetWorkspace(workspace);
        Assert.True(policy.IsApproved("file_access_delete"));

        policy.SetWorkspace(Path.Combine(Path.GetTempPath(), "another-approval-workspace"));
        Assert.False(policy.IsApproved("file_access_read"));
        Assert.True(policy.IsApproved(SandboxToolNames.RunShell));
        Assert.True(policy.IsApproved("ordinary_tool"));
    }

    [Fact]
    public void NewPolicy_DoesNotContainPreviousSessionGrants()
    {
        var previous = new SessionToolApprovalPolicy();
        previous.SetWorkspace(Path.GetTempPath());
        previous.Grant("file_access_read");

        Assert.False(new SessionToolApprovalPolicy().IsApproved("file_access_read"));
    }

    [Fact]
    public void Grant_OrdinaryToolDoesNotCrossRuntimeAgentBoundary()
    {
        var policy = new SessionToolApprovalPolicy();

        policy.Grant("shared_name", "first-agent");

        Assert.True(policy.IsApproved("shared_name", "first-agent"));
        Assert.False(policy.IsApproved("shared_name", "second-agent"));
    }

    [Fact]
    public void FileAccessNames_AreCaseSensitiveAndDoNotMatchOrdinaryTools()
    {
        var policy = new SessionToolApprovalPolicy();
        policy.SetWorkspace(Path.GetTempPath());

        policy.Grant("FILE_ACCESS_READ", "agent");

        Assert.True(policy.IsApproved("FILE_ACCESS_READ", "agent"));
        Assert.False(policy.IsApproved("file_access_read", "agent"));
    }
}
