using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class WorkflowDefinitionService(IWorkflowDefinitionRepository repository) : IWorkflowDefinitionService
{
    private readonly IWorkflowDefinitionRepository _repository = repository;

    public async Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync()
    {
        var workflows = await GetNormalizedWorkflowsAsync();
        return workflows
            .OrderBy(static workflow => workflow.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static workflow => workflow.WorkflowId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId)
    {
        var workflows = await GetNormalizedWorkflowsAsync();
        return workflows.FirstOrDefault(workflow => workflow.Id == workflowId);
    }

    public async Task CreateAsync(SavedWorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var workflows = await GetNormalizedWorkflowsAsync();
        var usedIds = workflows
            .Select(static workflow => workflow.Id)
            .Where(static id => id != Guid.Empty)
            .ToHashSet();

        if (workflow.Id == Guid.Empty || !usedIds.Add(workflow.Id))
        {
            workflow.Id = GenerateUniqueId(usedIds);
        }

        var now = DateTime.UtcNow;
        workflow.CreatedAt = now;
        workflow.UpdatedAt = now;
        NormalizeWorkflow(workflow);
        workflows.Add(workflow);
        await _repository.SaveAllAsync(workflows);
    }

    public async Task UpdateAsync(SavedWorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var workflows = await GetNormalizedWorkflowsAsync();
        var index = workflows.FindIndex(existing => existing.Id == workflow.Id);
        if (index == -1)
        {
            throw new KeyNotFoundException($"Workflow with ID {workflow.Id} not found");
        }

        var existing = workflows[index];
        workflow.CreatedAt = existing.CreatedAt;
        workflow.UpdatedAt = DateTime.UtcNow;
        NormalizeWorkflow(workflow);
        workflows[index] = workflow;
        await _repository.SaveAllAsync(workflows);
    }

    public async Task DeleteAsync(Guid workflowId)
    {
        var workflows = await GetNormalizedWorkflowsAsync();
        var existing = workflows.FirstOrDefault(workflow => workflow.Id == workflowId) ??
                       throw new KeyNotFoundException($"Workflow with ID {workflowId} not found");
        workflows.Remove(existing);
        await _repository.SaveAllAsync(workflows);
    }

    private async Task<List<SavedWorkflowDefinition>> GetNormalizedWorkflowsAsync()
    {
        var workflows = (await _repository.GetAllAsync()).ToList();
        var hasChanges = NormalizeIds(workflows);
        hasChanges = NormalizeWorkflows(workflows) || hasChanges;
        if (!hasChanges)
        {
            return workflows;
        }

        await _repository.SaveAllAsync(workflows);
        return workflows;
    }

    private static bool NormalizeIds(List<SavedWorkflowDefinition> workflows)
    {
        var usedIds = new HashSet<Guid>();
        var hasChanges = false;

        foreach (var workflow in workflows)
        {
            if (workflow.Id != Guid.Empty && usedIds.Add(workflow.Id))
            {
                continue;
            }

            workflow.Id = GenerateUniqueId(usedIds);
            hasChanges = true;
        }

        return hasChanges;
    }

    private static bool NormalizeWorkflows(List<SavedWorkflowDefinition> workflows)
    {
        var hasChanges = false;
        foreach (var workflow in workflows)
        {
            hasChanges = NormalizeWorkflow(workflow) || hasChanges;
        }

        return hasChanges;
    }

    private static bool NormalizeWorkflow(SavedWorkflowDefinition workflow)
    {
        var hasChanges = false;

        hasChanges = NormalizeString(workflow.Kind, WorkflowDefinitionKinds.Handoff, value => workflow.Kind = value) || hasChanges;
        hasChanges = NormalizeString(workflow.WorkflowId, string.Empty, value => workflow.WorkflowId = value) || hasChanges;
        hasChanges = NormalizeString(workflow.DisplayName, string.Empty, value => workflow.DisplayName = value) || hasChanges;
        hasChanges = NormalizeString(workflow.Description, string.Empty, value => workflow.Description = value) || hasChanges;
        hasChanges = NormalizeString(workflow.SourceCode, string.Empty, value => workflow.SourceCode = value) || hasChanges;

        return hasChanges;
    }

    private static bool NormalizeString(string? current, string fallback, Action<string> assign)
    {
        var normalized = current?.Trim();
        if (normalized is null)
        {
            normalized = fallback;
        }

        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        assign(normalized);
        return true;
    }

    private static Guid GenerateUniqueId(HashSet<Guid> usedIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!usedIds.Add(id));

        return id;
    }
}
