using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Options;

namespace ChatClient.Tests;

public sealed class AgentDefinitionDependencyGraphTests
{
    [Fact]
    public async Task AnalyzeAsync_ValidChain_ReturnsNodesAndEdgesWithoutProblems()
    {
        var c = Workflow("C", out var cId);
        var b = Workflow("B", out _, ("review", "Review", cId));
        var aWorkflow = Workflow("A", out var a, ("implementation", "Implementation", b.Id));
        var graph = CreateGraph([
            aWorkflow,
            b,
            c
        ]);

        var analysis = await graph.AnalyzeAsync(Reference(a));

        Assert.Empty(analysis.Problems);
        Assert.Equal(["A", "B", "C"], analysis.Nodes.Select(static node => node.DisplayName).Order().ToArray());
        Assert.Equal(2, analysis.Edges.Count);
        Assert.Contains(analysis.Edges, edge =>
            edge.ParticipantId == "implementation" &&
            edge.ParticipantDisplayName == "Implementation");
    }

    [Fact]
    public async Task AnalyzeAsync_Diamond_LoadsAndCompilesSharedWorkflowOnce()
    {
        var compiler = new CountingWorkflowDefinitionCompiler();
        var d = Workflow("D", out var dId);
        var b = Workflow("B", out var bId, ("to-d", "To D", dId));
        var c = Workflow("C", out var cId, ("to-d", "To D", dId));
        var aWorkflow = Workflow("A", out var a, ("to-b", "To B", bId), ("to-c", "To C", cId));
        var graph = CreateGraph(
            [
                aWorkflow,
                b,
                c,
                d
            ],
            compiler: compiler);

        var analysis = await graph.AnalyzeAsync(Reference(a));

        Assert.Empty(analysis.Problems);
        Assert.Equal(4, analysis.Nodes.Count);
        Assert.Equal(1, compiler.CompileCounts[dId]);
    }

    [Theory]
    [InlineData("A", "A")]
    [InlineData("A", "B", "A")]
    [InlineData("A", "B", "C", "A")]
    public async Task AnalyzeAsync_Cycles_ReturnDiagnosticWithParticipantPath(
        params string[] path)
    {
        var ids = path
            .Distinct()
            .ToDictionary(
                static name => name,
                static _ => Guid.NewGuid().ToString("D"),
                StringComparer.OrdinalIgnoreCase);
        var workflows = ids.Keys
            .Select(name =>
            {
                var index = Array.IndexOf(path, name);
                var next = index >= 0 && index + 1 < path.Length
                    ? path[index + 1]
                    : null;
                return next is null
                    ? Workflow(name, ids[name])
                    : Workflow(name, ids[name], new ParticipantReference(
                        $"to-{next.ToLowerInvariant()}",
                        $"To {next}",
                        new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, ids[next])));
            })
            .ToList();
        var root = ids[path[0]];
        var graph = CreateGraph(workflows);

        var analysis = await graph.AnalyzeAsync(Reference(root));

        var problem = Assert.Single(analysis.Problems);
        Assert.Contains("Workflow dependency cycle detected", problem.Message);
        Assert.Contains("A", problem.Message);
        Assert.Contains("participant", problem.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingWorkflow_ReturnsPathSpecificDiagnostic()
    {
        var missing = Guid.NewGuid().ToString("D");
        var graph = CreateGraph([
            Workflow("Release Review", out var root, ("implementation", "Implementation", missing))
        ]);

        var analysis = await graph.AnalyzeAsync(Reference(root));

        var problem = Assert.Single(analysis.Problems);
        Assert.Contains("Release Review", problem.Message);
        Assert.Contains("participant \"Implementation\"", problem.Message);
        Assert.Contains("references missing saved workflow", problem.Message);
        Assert.Contains(missing, problem.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingAgent_ReturnsPathSpecificDiagnostic()
    {
        var missingAgent = Guid.NewGuid().ToString("D");
        var graph = CreateGraph([
            Workflow(
                "Release Review",
                out var root,
                new ParticipantReference("reviewer", "Reviewer", new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    missingAgent)))
        ]);

        var analysis = await graph.AnalyzeAsync(Reference(root));

        var problem = Assert.Single(analysis.Problems);
        Assert.Contains("Release Review", problem.Message);
        Assert.Contains("participant \"Reviewer\"", problem.Message);
        Assert.Contains("references missing saved agent", problem.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidNestedWorkflow_ReturnsDiagnosticWithPath()
    {
        var invalid = Workflow("Implementation Pipeline", out var invalidId);
        var compiler = new CountingWorkflowDefinitionCompiler([invalidId]);
        var graph = CreateGraph(
            [
                Workflow("Release Review", out var root, ("implementation", "Implementation", invalidId)),
                invalid
            ],
            compiler: compiler);

        var analysis = await graph.AnalyzeAsync(Reference(root));

        var problem = Assert.Single(analysis.Problems);
        Assert.Contains("Release Review", problem.Message);
        Assert.Contains("participant \"Implementation\"", problem.Message);
        Assert.Contains("Implementation Pipeline", problem.Message);
        Assert.Contains("invalid", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_DepthLimit_IsConfigurable()
    {
        var validB = Workflow("B", out var validBId);
        var validA = Workflow("A", out var validRoot, ("to-b", "To B", validBId));
        var validGraph = CreateGraph(
            [
                validA,
                validB
            ],
            maximumDepth: 2);

        Assert.Empty((await validGraph.AnalyzeAsync(Reference(validRoot))).Problems);

        var invalidC = Workflow("C", out var invalidCId);
        var invalidB = Workflow("B", out var invalidBId, ("to-c", "To C", invalidCId));
        var invalidA = Workflow("A", out var invalidRoot, ("to-b", "To B", invalidBId));
        var invalidGraph = CreateGraph(
            [
                invalidA,
                invalidB,
                invalidC
            ],
            maximumDepth: 2);

        var problem = Assert.Single((await invalidGraph.AnalyzeAsync(Reference(invalidRoot))).Problems);
        Assert.Contains("Workflow nesting limit exceeded", problem.Message);
        Assert.Contains("A", problem.Message);
        Assert.Contains("B", problem.Message);
        Assert.Contains("C", problem.Message);
        Assert.Contains("participant \"To C\"", problem.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsCycleWhenGuidReferencesUseDifferentFormats()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = Workflow("A", aId.ToString("D"), new ParticipantReference(
            "to-b", "To B", new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, bId.ToString("N"))));
        var b = Workflow("B", bId.ToString("D"), new ParticipantReference(
            "to-a", "To A", new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, aId.ToString("D"))));
        var graph = CreateGraph([a, b]);

        var analysis = await graph.AnalyzeAsync(Reference(aId.ToString("N")));

        Assert.Contains(analysis.Problems, static problem =>
            problem.Message.Contains("Workflow dependency cycle detected", StringComparison.Ordinal));
    }

    private static AgentDefinitionDependencyGraph CreateGraph(
        IReadOnlyList<WorkflowRecord> workflows,
        CountingWorkflowDefinitionCompiler? compiler = null,
        int maximumDepth = 8,
        IReadOnlyList<AgentTemplateDefinition>? agents = null)
    {
        compiler ??= new CountingWorkflowDefinitionCompiler();
        compiler.SetWorkflows(workflows);

        return new AgentDefinitionDependencyGraph(
            new StubAgentTemplateService(agents ?? []),
            new StubWorkflowDefinitionService(workflows),
            compiler,
            new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService(agents ?? [])),
            new PassThroughWorkflowParticipantResolver(),
            Options.Create(new AgentRuntimeOptions
            {
                MaximumWorkflowNestingDepth = maximumDepth
            }));
    }

    private static WorkflowRecord Workflow(
        string name,
        out string id,
        params (string Id, string DisplayName, string ChildName)[] children)
    {
        id = Guid.NewGuid().ToString("D");
        return Workflow(
            name,
            id,
            children.Select(child => new ParticipantReference(
                child.Id,
                child.DisplayName,
                new AgentDefinitionReference(
                    AgentDefinitionKind.SavedWorkflow,
                    child.ChildName))).ToArray());
    }

    private static WorkflowRecord Workflow(
        string name,
        out string id)
    {
        id = Guid.NewGuid().ToString("D");
        return Workflow(name, id, []);
    }

    private static WorkflowRecord Workflow(
        string name,
        out string id,
        params ParticipantReference[] children)
    {
        id = Guid.NewGuid().ToString("D");
        return Workflow(name, id, children);
    }

    private static WorkflowRecord Workflow(
        string name,
        string id) =>
        Workflow(name, id, []);

    private static WorkflowRecord Workflow(
        string name,
        string id,
        params ParticipantReference[] children)
    {
        var workflow = new SequentialWorkflowDefinition
        {
            Id = id,
            DisplayName = name,
            Participants = children.Select(child => new WorkflowParticipantDefinition
            {
                Id = child.Id,
                Role = child.DisplayName,
                Source = new SavedDefinitionParticipantSource(child.Reference)
            }).ToList(),
            ParticipantOrder = children.Select(static child => child.Id).ToList()
        };

        return new WorkflowRecord(id, name, workflow);
    }

    private static AgentDefinitionReference Reference(string id) =>
        new(AgentDefinitionKind.SavedWorkflow, id);

    private sealed record ParticipantReference(
        string Id,
        string DisplayName,
        AgentDefinitionReference Reference);

    private sealed record WorkflowRecord(
        string Id,
        string DisplayName,
        IOrchestrationWorkflowDefinition Workflow);

    private sealed class CountingWorkflowDefinitionCompiler(
        IReadOnlyCollection<string>? invalidWorkflowIds = null) : IWorkflowDefinitionCompiler
    {
        private IReadOnlyDictionary<string, WorkflowRecord> _workflows =
            new Dictionary<string, WorkflowRecord>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> CompileCounts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void SetWorkflows(IReadOnlyList<WorkflowRecord> workflows)
        {
            _workflows = workflows.ToDictionary(
                static workflow => workflow.Id,
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<CompiledWorkflowDefinition> CompileAsync(
            string sourceCode,
            CancellationToken cancellationToken = default)
        {
            CompileCounts[sourceCode] = CompileCounts.GetValueOrDefault(sourceCode) + 1;
            if (invalidWorkflowIds?.Contains(sourceCode) == true)
            {
                throw new WorkflowCompilationException("invalid workflow");
            }

            if (!_workflows.TryGetValue(sourceCode, out var workflow))
            {
                throw new InvalidOperationException(
                    "Workflow source was not registered with the compiler.");
            }

            return Task.FromResult(new CompiledWorkflowDefinition
            {
                Kind = workflow.Workflow.Kind,
                WorkflowId = workflow.Id,
                DisplayName = workflow.DisplayName,
                Workflow = workflow.Workflow
            });
        }
    }

    private sealed class StubWorkflowDefinitionService(
        IReadOnlyList<WorkflowRecord> workflows) : IWorkflowDefinitionService
    {
        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SavedWorkflowDefinition>>(
                workflows.Select(ToSavedWorkflow).ToList());

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult(workflows
                .Where(workflow => Guid.Parse(workflow.Id) == workflowId)
                .Select(ToSavedWorkflow)
                .FirstOrDefault());

        public Task CreateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task UpdateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task DeleteAsync(Guid workflowId) => throw new NotSupportedException();

        private static SavedWorkflowDefinition ToSavedWorkflow(WorkflowRecord workflow) =>
            new()
            {
                Id = Guid.Parse(workflow.Id),
                WorkflowId = workflow.Id,
                DisplayName = workflow.DisplayName,
                SourceCode = workflow.Id
            };
    }

    private sealed class PassThroughWorkflowParticipantResolver : IWorkflowParticipantResolver
    {
        public Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
            IOrchestrationWorkflowDefinition workflow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResolvedWorkflowParticipant>>(
                workflow.Participants.Select(static participant =>
                {
                    var source = Assert.IsType<SavedDefinitionParticipantSource>(participant.Source);
                    return new ResolvedWorkflowParticipant
                    {
                        ParticipantId = participant.Id,
                        DisplayName = string.IsNullOrWhiteSpace(participant.Role)
                            ? participant.Id
                            : participant.Role,
                        Summary = participant.Summary,
                        RuntimeKind = source.Reference.Kind == AgentDefinitionKind.SavedWorkflow
                            ? AgentRuntimeKind.WorkflowAgent
                            : AgentRuntimeKind.LlmAgent,
                        Source = new ReferencedParticipantSource(source.Reference)
                    };
                }).ToList());
    }

    private sealed class StubAgentTemplateService(
        IReadOnlyList<AgentTemplateDefinition> agents) : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<AgentTemplateDefinition>>(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid agentId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == agentId));

        public Task CreateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task DeleteAsync(Guid agentId) => throw new NotSupportedException();
    }
}
