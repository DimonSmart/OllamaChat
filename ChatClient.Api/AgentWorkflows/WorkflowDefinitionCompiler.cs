using System.Reflection;
using ChatClient.Application.Services.Agentic;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ChatClient.Api.AgentWorkflows;

public interface IWorkflowDefinitionCompiler
{
    Task<CompiledWorkflowDefinition> CompileAsync(string sourceCode, CancellationToken cancellationToken = default);
}

public sealed class WorkflowDefinitionCompiler : IWorkflowDefinitionCompiler
{
    private static readonly IReadOnlyList<Assembly> ExplicitAssemblies =
    [
        typeof(object).Assembly,
        typeof(Enumerable).Assembly,
        typeof(AgentWorkflowDefinition).Assembly,
        typeof(AgentDefinitionBuilder).Assembly,
        typeof(BuiltInTaskSessionMcpServerTools).Assembly,
        typeof(AgentDescription).Assembly
    ];

    private static readonly ScriptOptions ScriptOptions = Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
        .WithReferences(GetScriptReferences())
        .WithImports(
            "System",
            "System.Linq",
            "System.Collections.Generic",
            "ChatClient.Api.AgentWorkflows",
            "ChatClient.Application.Services.Agentic",
            "ChatClient.Api.Services.BuiltIn",
            "ChatClient.Domain.Models");

    public async Task<CompiledWorkflowDefinition> CompileAsync(
        string sourceCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            throw new WorkflowCompilationException("Workflow source code is required.");
        }

        try
        {
            var state = await CSharpScript.RunAsync(
                code: sourceCode,
                options: ScriptOptions,
                cancellationToken: cancellationToken);

            var workflow = ExtractWorkflow(state);
            if (workflow is null)
            {
                throw new WorkflowCompilationException(
                    "Workflow source must return a supported orchestration workflow definition or assign it to a variable named 'workflow'.");
            }

            return new CompiledWorkflowDefinition
            {
                Kind = workflow.Kind,
                WorkflowId = workflow.Id,
                DisplayName = workflow.DisplayName,
                Description = workflow.Description,
                Workflow = workflow,
                HandoffWorkflow = workflow as AgentWorkflowDefinition,
                SequentialWorkflow = workflow as SequentialWorkflowDefinition,
                ConcurrentWorkflow = workflow as ConcurrentWorkflowDefinition,
                GroupChatWorkflow = workflow as GroupChatWorkflowDefinition
            };
        }
        catch (CompilationErrorException ex)
        {
            throw new WorkflowCompilationException(FormatCompilationErrors(ex.Diagnostics), ex);
        }
    }

    private static IOrchestrationWorkflowDefinition? ExtractWorkflow(ScriptState<object?> state)
    {
        if (state.ReturnValue is IOrchestrationWorkflowDefinition workflow)
        {
            return workflow;
        }

        var workflowVariable = state.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Name, "workflow", StringComparison.Ordinal));
        return workflowVariable?.Value as IOrchestrationWorkflowDefinition;
    }

    private static IReadOnlyList<Assembly> GetScriptReferences()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Concat(ExplicitAssemblies)
            .Where(static assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            .Distinct()
            .ToList();
    }

    private static string FormatCompilationErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var messages = diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic =>
            {
                var location = diagnostic.Location.GetLineSpan();
                if (!location.IsValid)
                {
                    return diagnostic.GetMessage();
                }

                var line = location.StartLinePosition.Line + 1;
                var column = location.StartLinePosition.Character + 1;
                return $"Line {line}, Col {column}: {diagnostic.GetMessage()}";
            })
            .ToArray();

        return messages.Length == 0
            ? "Workflow compilation failed."
            : string.Join(Environment.NewLine, messages);
    }
}

public sealed class CompiledWorkflowDefinition
{
    public required string Kind { get; init; }

    public required string WorkflowId { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public IOrchestrationWorkflowDefinition? Workflow { get; init; }

    public AgentWorkflowDefinition? HandoffWorkflow { get; init; }

    public SequentialWorkflowDefinition? SequentialWorkflow { get; init; }

    public ConcurrentWorkflowDefinition? ConcurrentWorkflow { get; init; }

    public GroupChatWorkflowDefinition? GroupChatWorkflow { get; init; }
}

public sealed class WorkflowCompilationException : Exception
{
    public WorkflowCompilationException(string message)
        : base(message)
    {
    }

    public WorkflowCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
