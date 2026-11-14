using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class LocalMarkdownPluginSkillsTests
{
    [Fact]
    public async Task SelectedPlugin_UsesNativeAgentFileSkillAndPreservesReferences()
    {
        var root = LocalMarkdownPluginServiceTests.CreateRoot();
        try
        {
            var directory = await LocalMarkdownPluginServiceTests.CreatePluginAsync(root, "idd-intent", "idd-help");
            var service = new LocalMarkdownPluginService(
                LocalMarkdownPluginServiceTests.Configuration(root), NullLogger<LocalMarkdownPluginService>.Instance);
            var registration = await service.RegisterAsync(directory);
            var profile = new AgentSkillsProfile { PluginIds = [registration.Id] };

            var discovered = await AgentSkillsDiscovery.DiscoverAsync(profile, null,
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            try
            {
                var skill = Assert.Single(discovered.Skills);
                Assert.Equal("idd-help", skill.Name);
                Assert.Equal(AgentSkillSourceKind.Plugin, skill.SourceKind);
                Assert.Equal(registration.Id, skill.PluginId);
                Assert.Equal("idd-intent", skill.PluginName);
                var source = Assert.IsType<AggregatingAgentSkillsSource>(discovered.Source);
                var native = Assert.IsType<AgentFileSkill>(
                    Assert.Single(await source.GetSkillsAsync(null!, TestContext.Current.CancellationToken)));
                var content = await native.GetContentAsync(TestContext.Current.CancellationToken);
                Assert.Contains("references/methodology.md", content);
                Assert.True(File.Exists(Path.Combine(directory, "skills", "idd-help", "references", "methodology.md")));
            }
            finally { discovered.Source?.Dispose(); }

            await service.SetEnabledAsync(registration.Id, false);
            var disabled = await AgentSkillsDiscovery.DiscoverAsync(profile, null,
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            Assert.Empty(disabled.Skills);
            Assert.Null(disabled.Source);

            await service.RemoveAsync(registration.Id);
            var missing = await AgentSkillsDiscovery.DiscoverAsync(profile, null,
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            Assert.Empty(missing.Skills);
            Assert.Contains(missing.Diagnostics, x => x.Contains("not found", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingSourcesKeepPrecedence_AndInvalidPluginsDoNotBlockOtherSkills()
    {
        var root = LocalMarkdownPluginServiceTests.CreateRoot();
        try
        {
            var plugin = await LocalMarkdownPluginServiceTests.CreatePluginAsync(root, "idd-intent", "idd-help");
            var other = await LocalMarkdownPluginServiceTests.CreatePluginAsync(root, "other-plugin", "idd-help");
            var service = new LocalMarkdownPluginService(
                LocalMarkdownPluginServiceTests.Configuration(root), NullLogger<LocalMarkdownPluginService>.Instance);
            var first = await service.RegisterAsync(plugin);
            var second = await service.RegisterAsync(other);

            var workspaceSkill = Directory.CreateDirectory(Path.Combine(root, "workspace", "skills", "idd-help"));
            await File.WriteAllTextAsync(Path.Combine(workspaceSkill.FullName, "SKILL.md"),
                "---\nname: idd-help\ndescription: Workspace wins\n---\nLocal.",
                TestContext.Current.CancellationToken);
            var profile = new AgentSkillsProfile
            {
                PluginIds = [first.Id, second.Id],
                FileSources = [new SkillFileSource { Directory = "skills", Patterns = ["**/SKILL.md"] }]
            };
            var found = await AgentSkillsDiscovery.DiscoverAsync(profile, Path.Combine(root, "workspace"),
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            try
            {
                Assert.Single(found.Skills);
                Assert.Equal(AgentSkillSourceKind.WorkspaceFile, found.Skills[0].SourceKind);
                Assert.Contains(found.Diagnostics, x => x.Contains("same name already loaded", StringComparison.Ordinal));
            }
            finally { found.Source?.Dispose(); }

            File.Delete(Path.Combine(plugin, ".codex-plugin", "plugin.json"));
            var withoutBroken = await AgentSkillsDiscovery.DiscoverAsync(profile, Path.Combine(root, "workspace"),
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            try
            {
                Assert.Single(withoutBroken.Skills);
                Assert.Contains(withoutBroken.Diagnostics, x => x.Contains("manifest", StringComparison.OrdinalIgnoreCase));
            }
            finally { withoutBroken.Source?.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConflictingPluginNames_ReportDiagnosticWithoutBlockingFirstPlugin()
    {
        var root = LocalMarkdownPluginServiceTests.CreateRoot();
        try
        {
            var firstDir = await LocalMarkdownPluginServiceTests.CreatePluginAsync(root, "same-name", "first");
            var secondDir = await LocalMarkdownPluginServiceTests.CreatePluginAsync(root, "other-directory", "second");
            await File.WriteAllTextAsync(Path.Combine(secondDir, ".codex-plugin", "plugin.json"),
                """{"name":"same-name","version":"1","description":"Duplicate","skills":"./skills/"}""",
                TestContext.Current.CancellationToken);
            var service = new LocalMarkdownPluginService(
                LocalMarkdownPluginServiceTests.Configuration(root), NullLogger<LocalMarkdownPluginService>.Instance);
            var first = await service.RegisterAsync(firstDir);
            var second = await service.RegisterAsync(secondDir);
            var found = await AgentSkillsDiscovery.DiscoverAsync(
                new AgentSkillsProfile { PluginIds = [first.Id, second.Id] }, null,
                NullLogger.Instance, TestContext.Current.CancellationToken, service);
            try
            {
                Assert.Equal("first", Assert.Single(found.Skills).Name);
                Assert.Contains(found.Diagnostics, x => x.Contains("same plugin name", StringComparison.Ordinal));
            }
            finally { found.Source?.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
