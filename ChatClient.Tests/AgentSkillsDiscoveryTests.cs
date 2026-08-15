using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class AgentSkillsDiscoveryTests
{
    [Fact]
    public async Task Discover_UsesNativeFileSkillAndRetainsResourcesAndScripts()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-skills-discovery", Guid.NewGuid().ToString("N")));
        try
        {
            var skill = Directory.CreateDirectory(Path.Combine(workspace.FullName, ".agents", "skills", "code-review"));
            Directory.CreateDirectory(Path.Combine(skill.FullName, "references"));
            Directory.CreateDirectory(Path.Combine(skill.FullName, "scripts"));
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "---\nname: code-review\ndescription: Review code\n---\nUse the referenced checklist.", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "references", "checklist.md"), "Review checklist", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "scripts", "analyze.ps1"), "Write-Output analysis", TestContext.Current.CancellationToken);

            var profile = new AgentSkillsProfile
            {
                FileSources = [new SkillFileSource { Directory = ".agents/skills", Patterns = ["**/SKILL.md"] }]
            };

            var result = await AgentSkillsDiscovery.DiscoverAsync(profile, workspace.FullName, NullLogger.Instance, TestContext.Current.CancellationToken);

            var discovered = Assert.Single(result.Skills);
            Assert.Equal("code-review", discovered.Name);
            Assert.Equal(skill.FullName, discovered.SourcePath);
            var source = Assert.IsType<AggregatingAgentSkillsSource>(result.Source);
            var nativeSkill = Assert.IsType<AgentFileSkill>(Assert.Single(await source.GetSkillsAsync(null!, CancellationToken.None)));
            var skillContent = await nativeSkill.GetContentAsync(TestContext.Current.CancellationToken);
            Assert.Contains("references/checklist.md", skillContent);
            Assert.Contains("scripts/analyze.ps1", skillContent);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverAsync_AcceptsSkillDirectoryAsSourceRoot()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-skills-discovery", Guid.NewGuid().ToString("N")));
        try
        {
            var skill = Directory.CreateDirectory(Path.Combine(workspace.FullName, "code-review"));
            await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "---\nname: code-review\ndescription: Review code\n---\n", TestContext.Current.CancellationToken);
            var profile = new AgentSkillsProfile { FileSources = [new SkillFileSource { Directory = skill.FullName, Patterns = ["SKILL.md"] }] };

            var result = await AgentSkillsDiscovery.DiscoverAsync(profile, workspace.FullName, NullLogger.Instance, TestContext.Current.CancellationToken);
            Assert.Equal("code-review", Assert.Single(result.Skills).Name);
            result.Source.Dispose();
        }
        finally { workspace.Delete(recursive: true); }
    }

    [Fact]
    public async Task DiscoverAsync_KeepsSkillsStableAfterFilesystemChanges()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-skills-discovery", Guid.NewGuid().ToString("N")));
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(workspace.FullName, "skills", "first"));
            await File.WriteAllTextAsync(Path.Combine(first.FullName, "SKILL.md"), "---\nname: first\ndescription: First skill\n---\nUse this skill.", TestContext.Current.CancellationToken);
            var result = await AgentSkillsDiscovery.DiscoverAsync(new AgentSkillsProfile { FileSources = [new SkillFileSource { Directory = "skills", Patterns = ["**/SKILL.md"] }] }, workspace.FullName, NullLogger.Instance, TestContext.Current.CancellationToken);
            var second = Directory.CreateDirectory(Path.Combine(workspace.FullName, "skills", "second"));
            await File.WriteAllTextAsync(Path.Combine(second.FullName, "SKILL.md"), "---\nname: second\ndescription: Second skill\n---\nUse this skill.", TestContext.Current.CancellationToken);

            Assert.Equal(["first"], (await result.Source!.GetSkillsAsync(null!, TestContext.Current.CancellationToken)).Select(x => x.Frontmatter.Name));
            result.Source.Dispose();
        }
        finally { workspace.Delete(recursive: true); }
    }

    [Fact]
    public async Task DiscoverAsync_ReportsDuplicatesAndDoesNotDiscardValidSkillsForInvalidCandidates()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-skills-discovery", Guid.NewGuid().ToString("N")));
        try
        {
            var workspaceSkill = Directory.CreateDirectory(Path.Combine(workspace.FullName, "skills", "code-review"));
            var claudeSkill = Directory.CreateDirectory(Path.Combine(workspace.FullName, ".claude", "skills", "code-review"));
            var brokenSkill = Directory.CreateDirectory(Path.Combine(workspace.FullName, "skills", "broken"));
            await File.WriteAllTextAsync(Path.Combine(workspaceSkill.FullName, "SKILL.md"), "---\nname: code-review\ndescription: Workspace skill\n---\nUse this skill.", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(claudeSkill.FullName, "SKILL.md"), "---\nname: code-review\ndescription: Claude skill\n---\nUse this skill.", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(brokenSkill.FullName, "SKILL.md"), "invalid", TestContext.Current.CancellationToken);
            var profile = new AgentSkillsProfile { IncludeClaudeSkills = true, FileSources = [new SkillFileSource { Directory = "skills", Patterns = ["**/SKILL.md"] }] };

            var result = await AgentSkillsDiscovery.DiscoverAsync(profile, workspace.FullName, NullLogger.Instance, TestContext.Current.CancellationToken);

            Assert.Equal(workspaceSkill.FullName, Assert.Single(result.Skills).SourcePath);
            Assert.Contains(result.Diagnostics, x => x.Contains("same name already loaded", StringComparison.Ordinal));
            Assert.Contains(result.Diagnostics, x => x.Contains(brokenSkill.FullName, StringComparison.Ordinal));
            result.Source!.Dispose();
        }
        finally { workspace.Delete(recursive: true); }
    }

}
