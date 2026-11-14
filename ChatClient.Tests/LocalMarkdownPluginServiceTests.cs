using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class LocalMarkdownPluginServiceTests
{
    [Fact]
    public async Task Register_PersistsMetadataPathAndEnabledFlag_WithoutCopyingFiles()
    {
        var root = CreateRoot();
        try
        {
            var pluginDirectory = await CreatePluginAsync(root, "idd-intent", "idd-help");
            var config = Configuration(root);
            var service = new LocalMarkdownPluginService(config, NullLogger<LocalMarkdownPluginService>.Instance);
            var registration = await service.RegisterAsync(pluginDirectory);
            var inspected = service.Inspect(registration);

            Assert.Equal("idd-intent", inspected.Name);
            Assert.Equal("0.0.0", inspected.Version);
            Assert.Equal("Local test plugin", inspected.Description);
            Assert.Equal(["idd-help"], inspected.Skills);
            Assert.Empty(inspected.Diagnostics);
            Assert.Equal(Path.Combine(pluginDirectory, "skills"), inspected.SkillsDirectory);
            Assert.NotEqual(Guid.Empty, registration.Id);

            var restarted = new LocalMarkdownPluginService(config, NullLogger<LocalMarkdownPluginService>.Instance);
            var recovered = Assert.Single(await restarted.GetAllAsync());
            Assert.Equal(registration.Id, recovered.Id);
            await restarted.SetEnabledAsync(recovered.Id, false);
            Assert.False(Assert.Single(await service.GetAllAsync()).Enabled);

            await service.RemoveAsync(recovered.Id);
            Assert.Empty(await restarted.GetAllAsync());
            Assert.True(File.Exists(Path.Combine(pluginDirectory, "skills", "idd-help", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(pluginDirectory, "skills", "idd-help", "references", "methodology.md")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidManifestAndDuplicateDirectory_DoNotDamageExistingRegistrations()
    {
        var root = CreateRoot();
        try
        {
            var valid = await CreatePluginAsync(root, "idd-intent", "idd-help");
            var service = new LocalMarkdownPluginService(Configuration(root), NullLogger<LocalMarkdownPluginService>.Instance);
            var first = await service.RegisterAsync(valid);
            await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterAsync(valid));

            var broken = Path.Combine(root, "broken");
            Directory.CreateDirectory(Path.Combine(broken, ".codex-plugin"));
            await File.WriteAllTextAsync(Path.Combine(broken, ".codex-plugin", "plugin.json"),
                "{not-json", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterAsync(broken));
            Assert.Equal(first.Id, Assert.Single(await service.GetAllAsync()).Id);

            File.Delete(Path.Combine(valid, ".codex-plugin", "plugin.json"));
            var inspection = service.Inspect(first);
            Assert.Contains(inspection.Diagnostics, x => x.Contains("manifest", StringComparison.OrdinalIgnoreCase));
            Assert.Null(inspection.SkillsDirectory);
            Assert.Single(await service.GetAllAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManifestCannotEscapePluginDirectory()
    {
        var root = CreateRoot();
        try
        {
            var path = await CreatePluginAsync(root, "escape-test", "one");
            await File.WriteAllTextAsync(Path.Combine(path, ".codex-plugin", "plugin.json"),
                """{"name":"escape-test","version":"1","description":"test","skills":"../other"}""",
                TestContext.Current.CancellationToken);
            var service = new LocalMarkdownPluginService(Configuration(root), NullLogger<LocalMarkdownPluginService>.Instance);
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterAsync(path));
            Assert.Contains("leaves the plugin directory", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ollamachat-markdown-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static IConfiguration Configuration(string root) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMarkdownPlugins:FilePath"] = Path.Combine(root, "registry.json")
        })
        .Build();

    internal static async Task<string> CreatePluginAsync(string root, string pluginName, string skillName)
    {
        var plugin = Path.Combine(root, pluginName);
        Directory.CreateDirectory(Path.Combine(plugin, ".codex-plugin"));
        var skill = Directory.CreateDirectory(Path.Combine(plugin, "skills", skillName));
        Directory.CreateDirectory(Path.Combine(skill.FullName, "references"));
        await File.WriteAllTextAsync(Path.Combine(plugin, ".codex-plugin", "plugin.json"),
            System.Text.Json.JsonSerializer.Serialize(new { name = pluginName, version = "0.0.0", description = "Local test plugin", skills = "./skills/", unknown = new { extension = true } }),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: Sample skill\n---\nRead references/methodology.md.",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skill.FullName, "references", "methodology.md"),
            "The canonical methodology describes product intent.",
            TestContext.Current.CancellationToken);
        return plugin;
    }
}
