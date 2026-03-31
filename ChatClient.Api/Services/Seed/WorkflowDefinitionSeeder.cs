using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Text.Json;

namespace ChatClient.Api.Services.Seed;

public sealed class WorkflowDefinitionSeeder(
    IWorkflowDefinitionRepository repository,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<WorkflowDefinitionSeeder> logger)
{
    private readonly IWorkflowDefinitionRepository _repository = repository;
    private readonly IWorkflowDefinitionCompiler _workflowDefinitionCompiler = workflowDefinitionCompiler;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<WorkflowDefinitionSeeder> _logger = logger;

    public async Task SeedAsync()
    {
        var existing = (await _repository.GetAllAsync()).ToList();
        var seeded = await LoadSeedWorkflowsAsync();
        if (seeded.Count == 0)
        {
            return;
        }

        var existingWorkflowIds = existing
            .Select(static workflow => workflow.WorkflowId)
            .Where(static workflowId => !string.IsNullOrWhiteSpace(workflowId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasChanges = false;

        foreach (var workflow in seeded)
        {
            if (!existingWorkflowIds.Add(workflow.WorkflowId))
            {
                continue;
            }

            existing.Add(workflow);
            hasChanges = true;
        }

        if (hasChanges || existing.Count == 0)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    private async Task<List<SavedWorkflowDefinition>> LoadSeedWorkflowsAsync()
    {
        var seedPath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["WorkflowDefinitions:SeedFilePath"],
            "workflow_definitions.json");

        if (File.Exists(seedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var seeded = JsonSerializer.Deserialize<List<SavedWorkflowDefinition>>(
                    json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (seeded is { Count: > 0 })
                {
                    return seeded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed workflow definitions from {SeedPath}", seedPath);
            }
        }

        List<SavedWorkflowDefinition> fallback = [];
        foreach (var template in WorkflowCodeTemplates.StarterTemplates)
        {
            fallback.Add(await BuildSavedWorkflowAsync(template.SourceCode));
        }

        return fallback;
    }

    private async Task<SavedWorkflowDefinition> BuildSavedWorkflowAsync(string sourceCode)
    {
        var compiled = await _workflowDefinitionCompiler.CompileAsync(sourceCode);
        var now = DateTime.UtcNow;

        return new SavedWorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Kind = compiled.Kind,
            WorkflowId = compiled.WorkflowId,
            DisplayName = compiled.DisplayName,
            Description = compiled.Description,
            SourceCode = sourceCode,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
