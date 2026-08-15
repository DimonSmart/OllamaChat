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

            var result = AgentSkillsDiscovery.Discover(profile, workspace.FullName, NullLogger.Instance);

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

}
