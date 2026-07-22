using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Text.Json;

namespace ChatClient.Api.Services.Seed;

public sealed class AgentTemplateSeeder(
    IAgentTemplateRepository repository,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AgentTemplateSeeder> logger)
{
    private static readonly Guid FallbackDefaultAssistantId = Guid.Parse("8d0f96a8-f827-4529-a5e9-c924dad2b6fc");
    private static readonly Guid FallbackCodeAssistantId = Guid.Parse("2e8a9f16-d8a6-40ee-9545-c5f84fc18f50");

    private readonly IAgentTemplateRepository _repository = repository;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<AgentTemplateSeeder> _logger = logger;

    public async Task SeedAsync()
    {
        var existing = (await _repository.GetAllAsync()).ToList();
        var seeded = await LoadSeedTemplatesAsync();
        if (seeded.Count == 0)
        {
            return;
        }

        var hasChanges = false;

        foreach (var template in seeded)
        {
            if (existing.Any(existingTemplate => existingTemplate.Id == template.Id))
            {
                continue;
            }

            existing.Add(template.Clone());
            hasChanges = true;
        }

        if (hasChanges || existing.Count == 0)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    public async Task RestoreSeededAsync()
    {
        var existing = (await _repository.GetAllAsync()).ToList();
        var seeded = await LoadSeedTemplatesAsync();
        if (seeded.Count == 0)
        {
            return;
        }

        var hasChanges = false;

        foreach (var template in seeded)
        {
            var existingIndex = existing.FindIndex(existingTemplate => existingTemplate.Id == template.Id);
            if (existingIndex < 0)
            {
                existing.Add(template.Clone());
                hasChanges = true;
                continue;
            }

            hasChanges = UpsertSeededTemplate(existing, existingIndex, template) || hasChanges;
        }

        if (hasChanges)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    private async Task<List<AgentTemplateDefinition>> LoadSeedTemplatesAsync()
    {
        var seedPath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["AgentTemplates:SeedFilePath"],
            "agent_templates.json");

        if (File.Exists(seedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var seeded = JsonSerializer.Deserialize<List<AgentTemplateDefinition>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (seeded is { Count: > 0 })
                {
                    return seeded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed agent templates from {SeedPath}", seedPath);
            }
        }

        return CreateFallbackAgents();
    }

    private static bool UpsertSeededTemplate(
        List<AgentTemplateDefinition> templates,
        int index,
        AgentTemplateDefinition seeded)
    {
        var existing = templates[index];
        var replacement = seeded.Clone();
        replacement.Id = existing.Id;
        replacement.CreatedAt = existing.CreatedAt == default
            ? seeded.CreatedAt
            : existing.CreatedAt;
        replacement.UpdatedAt = DateTime.UtcNow;

        if (AreEquivalent(existing, replacement))
        {
            return false;
        }

        templates[index] = replacement;
        return true;
    }

    private static bool AreEquivalent(AgentTemplateDefinition left, AgentTemplateDefinition right)
    {
        return left.Id == right.Id &&
               string.Equals(left.AgentName, right.AgentName, StringComparison.Ordinal) &&
               string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
               string.Equals(left.Content, right.Content, StringComparison.Ordinal) &&
               string.Equals(left.ShortName, right.ShortName, StringComparison.Ordinal) &&
               string.Equals(left.AvatarText, right.AvatarText, StringComparison.Ordinal) &&
               string.Equals(left.ModelName, right.ModelName, StringComparison.Ordinal) &&
               left.LlmId == right.LlmId &&
               left.Temperature == right.Temperature &&
               left.RepeatPenalty == right.RepeatPenalty &&
               left.FunctionSettings.AutoSelectCount == right.FunctionSettings.AutoSelectCount &&
               HaveEquivalentBindings(left.McpServerBindings, right.McpServerBindings);
    }

    private static bool HaveEquivalentBindings(
        IReadOnlyList<McpServerSessionBinding> left,
        IReadOnlyList<McpServerSessionBinding> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!HaveEquivalentBinding(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveEquivalentBinding(
        McpServerSessionBinding left,
        McpServerSessionBinding right)
    {
        return left.BindingId == right.BindingId &&
               left.ServerId == right.ServerId &&
               string.Equals(left.ServerName, right.ServerName, StringComparison.Ordinal) &&
               string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
               left.Enabled == right.Enabled &&
               left.SelectAllTools == right.SelectAllTools &&
               left.SelectedTools.SequenceEqual(right.SelectedTools, StringComparer.Ordinal) &&
               left.Roots.SequenceEqual(right.Roots, StringComparer.Ordinal) &&
               left.Parameters.Count == right.Parameters.Count &&
               left.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                   .SequenceEqual(
                       right.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                       KeyValuePairComparer.Instance);
    }

    private static List<AgentTemplateDefinition> CreateFallbackAgents()
    {
        return
        [
            new AgentTemplateDefinition
            {
                Id = FallbackDefaultAssistantId,
                AgentName = "Default Assistant",
                Content = "You are a polite and helpful assistant.\n\nMandatory personalization rule for EVERY user message:\n1) Before writing any final answer, first call MCP tool `prefs_get` with key `displayName` (aliases: `name`, `preferred_name`).\n2) If the name is missing, use elicitation to ask the user and save it.\n3) Then answer and address the user by name naturally at least once in the first sentence.\n\nNever skip this lookup, even for very simple questions (for example: current time). If lookup fails, continue politely without using a name.",
            },
            new AgentTemplateDefinition
            {
                Id = FallbackCodeAssistantId,
                AgentName = "Code Assistant",
                Content = "You are a coding assistant. Help the user write and understand code.",
            }
        ];
    }

    private sealed class KeyValuePairComparer : IEqualityComparer<KeyValuePair<string, string?>>
    {
        public static KeyValuePairComparer Instance { get; } = new();

        public bool Equals(KeyValuePair<string, string?> x, KeyValuePair<string, string?> y)
        {
            return string.Equals(x.Key, y.Key, StringComparison.Ordinal) &&
                   string.Equals(x.Value, y.Value, StringComparison.Ordinal);
        }

        public int GetHashCode(KeyValuePair<string, string?> obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Key),
                obj.Value is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Value));
        }
    }
}

