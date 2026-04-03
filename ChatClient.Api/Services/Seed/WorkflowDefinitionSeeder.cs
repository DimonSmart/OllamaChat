using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;

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

        var hasChanges = false;

        foreach (var workflow in seeded)
        {
            var existingIndex = existing.FindIndex(existingWorkflow =>
                string.Equals(existingWorkflow.WorkflowId, workflow.WorkflowId, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                existing.Add(workflow);
                hasChanges = true;
                continue;
            }

            hasChanges = UpsertSeededWorkflow(existing[existingIndex], workflow) || hasChanges;
        }

        if (hasChanges || existing.Count == 0)
        {
            await _repository.SaveAllAsync(existing);
        }
    }

    private async Task<List<SavedWorkflowDefinition>> LoadSeedWorkflowsAsync()
    {
        var seedDirectory = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["WorkflowDefinitions:SeedDirectoryPath"],
            "workflows");

        if (!Directory.Exists(seedDirectory))
        {
            _logger.LogInformation("Workflow seed directory was not found: {SeedDirectory}", seedDirectory);
            return [];
        }

        List<SavedWorkflowDefinition> seeded = [];
        foreach (var sourcePath in Directory.EnumerateFiles(seedDirectory, "*.workflow.csx", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var sourceCode = await File.ReadAllTextAsync(sourcePath);
                seeded.Add(await BuildSavedWorkflowAsync(sourceCode));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed workflow definition from {SourcePath}", sourcePath);
            }
        }

        return seeded;
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

    private static bool UpsertSeededWorkflow(
        SavedWorkflowDefinition existing,
        SavedWorkflowDefinition seeded)
    {
        var hasChanges = false;

        hasChanges = UpdateValue(existing.Kind, seeded.Kind, value => existing.Kind = value) || hasChanges;
        hasChanges = UpdateValue(existing.WorkflowId, seeded.WorkflowId, value => existing.WorkflowId = value) || hasChanges;
        hasChanges = UpdateValue(existing.DisplayName, seeded.DisplayName, value => existing.DisplayName = value) || hasChanges;
        hasChanges = UpdateValue(existing.Description, seeded.Description, value => existing.Description = value) || hasChanges;
        hasChanges = UpdateValue(existing.SourceCode, seeded.SourceCode, value => existing.SourceCode = value) || hasChanges;

        if (!hasChanges)
        {
            return false;
        }

        existing.UpdatedAt = seeded.UpdatedAt;
        if (existing.CreatedAt == default)
        {
            existing.CreatedAt = seeded.CreatedAt;
        }

        return true;
    }

    private static bool UpdateValue(
        string current,
        string next,
        Action<string> assign)
    {
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return false;
        }

        assign(next);
        return true;
    }
}
