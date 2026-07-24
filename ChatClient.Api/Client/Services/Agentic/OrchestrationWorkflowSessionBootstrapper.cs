using System.Globalization;
using System.Reflection;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowSessionBootstrapper(
    ILogger<OrchestrationWorkflowSessionBootstrapper> logger,
    IModelCapabilityService modelCapabilityService,
    TaskSessionStore taskSessionStore,
    MarkdownDocumentIntakeService documentIntakeService,
    AgenticRuntimeAgentFactory runtimeAgentFactory)
{
    private static readonly Lazy<MethodInfo?> GetDescriptiveIdMethod = new(static () =>
        Type.GetType(
                "Microsoft.Agents.AI.Workflows.AIAgentExtensions, Microsoft.Agents.AI.Workflows",
                throwOnError: false)?
            .GetMethod(
                "GetDescriptiveId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(AIAgent)],
                modifiers: null));

    public async Task<OrchestrationWorkflowSessionBootstrapResult> BootstrapAsync(
        OrchestrationWorkflowSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Participants.Count == 0 && request.Agents.Count == 0)
        {
            throw new ArgumentException("At least one workflow participant must be provided.", nameof(request));
        }

        var stage = "validate-agents";

        try
        {
            if (request.Participants.Count == 0)
            {
                await ValidateResolvedAgentsAsync(request.Agents, cancellationToken);
            }

            stage = "validate-workflow";
            ValidateWorkflowDefinition(request.Workflow, request.Participants, request.Agents);
            var normalizedStartInputs = NormalizeStartInputs(request.Workflow, request.StartInputs);
            var normalizedParameterValues = NormalizeParameterValues(request.Workflow, normalizedStartInputs);

            stage = "resolve-runtime-configuration";
            var runtimeWorkflow = ResolveRuntimeConfiguration(request.Workflow, normalizedParameterValues);

            stage = "create-task-session";
            var session = await taskSessionStore.CreateSessionAsync(
                request.SessionTitle,
                request.SessionDescription,
                cancellationToken);

            stage = "set-initial-phase";
            await taskSessionStore.SetPhaseAsync(session.SessionId, "intake", cancellationToken);

            foreach (var startInput in normalizedStartInputs)
            {
                var definition = runtimeWorkflow.StartInputs.First(input =>
                    string.Equals(input.Key, startInput.Key, StringComparison.OrdinalIgnoreCase));

                if (definition.Kind == WorkflowStartInputKind.MarkdownDocument)
                {
                    stage = $"prepare-document:{definition.Key}";
                    var prepared = await PrepareDocumentAsync(definition, startInput, cancellationToken);
                    if (prepared is null)
                    {
                        continue;
                    }

                    stage = $"attach-document:{definition.Key}";
                    await taskSessionStore.AttachDocumentAsync(
                        session.SessionId,
                        definition.Key,
                        prepared.Markdown,
                        prepared.Title,
                        prepared.SourceFile,
                        cancellationToken);
                    continue;
                }

                stage = $"attach-parameter:{definition.Key}";
                await taskSessionStore.SetParameterAsync(
                    session.SessionId,
                    definition.Key,
                    MapParameterValueKind(definition.Kind),
                    normalizedParameterValues[definition.Key],
                    cancellationToken);
            }

            List<OrchestrationWorkflowRuntimeAgentRegistration> runtimeAgents = [];
            List<ResolvedChatAgent> sessionBoundAgents = [];
            if (request.Participants.Count > 0)
            {
                var participantInvoker = request.ParticipantInvoker
                    ?? throw new InvalidOperationException(
                        "Runtime workflow participants require a participant invoker.");

                foreach (var participant in request.Participants)
                {
                    stage = $"adapt-runtime-participant:{participant.Id}";
                    var resolvedParticipant = request.ResolvedParticipants.FirstOrDefault(candidate =>
                        string.Equals(candidate.ParticipantId, participant.Id, StringComparison.OrdinalIgnoreCase));
                    var agent = new AgentRuntimeAIAgentAdapter(
                        participant,
                        resolvedParticipant,
                        request.CreationContext,
                        request.ParentRunContext,
                        participantInvoker);

                    runtimeAgents.Add(new OrchestrationWorkflowRuntimeAgentRegistration(
                        participant.Id,
                        participant.DisplayName,
                        agent,
                        TryGetAgentExecutorId(agent)));
                }
            }
            else
            {
                stage = "bind-task-session";
                sessionBoundAgents = request.Agents
                    .Select(agent => BindTaskSession(agent, session.SessionId))
                    .ToList();

                foreach (var sessionBoundAgent in sessionBoundAgents)
                {
                    stage = $"build-runtime-agent:{sessionBoundAgent.Agent.AgentId}";
                    var runtimeRequest = sessionBoundAgent.Agent
                        .ForRun()
                        .UsingModel(sessionBoundAgent.Model)
                        .WithConfiguration(request.Configuration)
                        .WithConversation([])
                        .WithUserMessage(string.Empty)
                        .Build();
                    var builtAgent = await runtimeAgentFactory.CreateAsync(
                        runtimeRequest,
                        requireFunctionCalling: true,
                        cancellationToken: cancellationToken);

                    runtimeAgents.Add(new OrchestrationWorkflowRuntimeAgentRegistration(
                        sessionBoundAgent.Agent.AgentId,
                        sessionBoundAgent.Agent.AgentName,
                        builtAgent.Agent,
                        TryGetAgentExecutorId(builtAgent.Agent)));
                }
            }

            return new OrchestrationWorkflowSessionBootstrapResult(
                session.SessionId,
                new OrchestrationWorkflowSessionStartRequest
                {
                    Workflow = runtimeWorkflow,
                    Participants = request.Participants,
                    ResolvedParticipants = request.ResolvedParticipants,
                    Agents = request.Participants.Count == 0 ? sessionBoundAgents : [],
                    ParticipantInvoker = request.ParticipantInvoker,
                    Configuration = request.Configuration,
                    CreationContext = request.CreationContext,
                    ParentRunContext = request.ParentRunContext,
                    SessionTitle = request.SessionTitle,
                    SessionDescription = request.SessionDescription,
                    StartInputs = normalizedStartInputs
                },
                runtimeAgents);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to bootstrap orchestration workflow session at stage {Stage}. WorkflowId={WorkflowId}, AgentCount={AgentCount}",
                stage,
                request.Workflow.Id,
                request.Participants.Count + request.Agents.Count);
            throw;
        }
    }

    private async Task<MarkdownDocumentIntakeResult?> PrepareDocumentAsync(
        WorkflowStartInputDefinition definition,
        OrchestrationWorkflowStartInputValue input,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.Value))
        {
            return documentIntakeService.PrepareMarkdown(input.Value, definition.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(input.SourceFile))
        {
            return await documentIntakeService.ReadDocumentAsync(input.SourceFile, cancellationToken);
        }

        return null;
    }

    private static void ValidateWorkflowDefinition(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<WorkflowRuntimeParticipant> participants,
        IReadOnlyList<ResolvedChatAgent> agents)
    {
        var workflowAgentIds = workflow.Participants
            .Select(static agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedAgentIds = participants.Count > 0
            ? participants.Select(static participant => participant.Id)
            : agents.Select(static agent => agent.Agent.AgentId);

        var resolvedAgentSet = resolvedAgentIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!workflowAgentIds.SetEquals(resolvedAgentSet))
        {
            throw new InvalidOperationException(
                "Resolved workflow agents do not match the workflow definition.");
        }
    }

    private static IReadOnlyList<OrchestrationWorkflowStartInputValue> NormalizeStartInputs(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationWorkflowStartInputValue> providedInputs)
    {
        var definitionsByKey = workflow.StartInputs.ToDictionary(
            static input => input.Key,
            StringComparer.OrdinalIgnoreCase);
        var providedByKey = providedInputs.ToDictionary(
            static input => input.Key,
            StringComparer.OrdinalIgnoreCase);

        var unknownKey = providedByKey.Keys.FirstOrDefault(key => !definitionsByKey.ContainsKey(key));
        if (unknownKey is not null)
        {
            throw new InvalidOperationException(
                $"Workflow start input '{unknownKey}' is not defined.");
        }

        List<OrchestrationWorkflowStartInputValue> normalizedInputs = [];

        foreach (var definition in workflow.StartInputs)
        {
            providedByKey.TryGetValue(definition.Key, out var provided);

            if (definition.Kind == WorkflowStartInputKind.MarkdownDocument)
            {
                if (provided is not null &&
                    (!string.IsNullOrWhiteSpace(provided.Value) ||
                     !string.IsNullOrWhiteSpace(provided.SourceFile)))
                {
                    normalizedInputs.Add(new OrchestrationWorkflowStartInputValue
                    {
                        Key = definition.Key,
                        Value = provided.Value,
                        SourceFile = provided.SourceFile
                    });
                    continue;
                }

                if (definition.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"Workflow start input '{definition.DisplayName}' is required.");
                }

                continue;
            }

            var value = provided?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = definition.DefaultValue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (definition.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"Workflow start input '{definition.DisplayName}' is required.");
                }

                continue;
            }

            normalizedInputs.Add(new OrchestrationWorkflowStartInputValue
            {
                Key = definition.Key,
                Value = value
            });
        }

        return normalizedInputs;
    }

    private static IReadOnlyDictionary<string, string> NormalizeParameterValues(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationWorkflowStartInputValue> normalizedInputs)
    {
        var inputsByKey = normalizedInputs.ToDictionary(
            static input => input.Key,
            StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in workflow.StartInputs)
        {
            if (definition.Kind == WorkflowStartInputKind.MarkdownDocument ||
                !inputsByKey.TryGetValue(definition.Key, out var input))
            {
                continue;
            }

            values[definition.Key] = NormalizeParameterValue(definition, input);
        }

        return values;
    }

    private static IOrchestrationWorkflowDefinition ResolveRuntimeConfiguration(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, string> parameterValues)
    {
        if (workflow is not GroupChatWorkflowDefinition groupChat ||
            groupChat.Manager.Kind != GroupChatWorkflowManagerKind.Programmable ||
            groupChat.Manager.Program is null)
        {
            return workflow;
        }

        var maximumIterations = groupChat.Manager.Program.ResolveMaximumIterations(
            new WorkflowStartValues(parameterValues),
            groupChat.Manager.MaximumIterations);

        if (maximumIterations == groupChat.Manager.MaximumIterations)
        {
            return workflow;
        }

        return new GroupChatWorkflowDefinition
        {
            Id = groupChat.Id,
            DisplayName = groupChat.DisplayName,
            Description = groupChat.Description,
            Execution = groupChat.Execution,
            StartInputs = groupChat.StartInputs,
            Participants = groupChat.Participants,
            ParticipantIds = groupChat.ParticipantIds,
            Manager = new GroupChatWorkflowManagerDefinition
            {
                Kind = groupChat.Manager.Kind,
                MaximumIterations = maximumIterations,
                ImplementationKey = groupChat.Manager.ImplementationKey,
                Program = groupChat.Manager.Program,
                ProgramDisplayName = groupChat.Manager.ProgramDisplayName
            }
        };
    }

    private static string NormalizeParameterValue(
        WorkflowStartInputDefinition definition,
        OrchestrationWorkflowStartInputValue input)
    {
        var value = input.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' requires a value.");
        }

        return definition.Kind switch
        {
            WorkflowStartInputKind.Text => value,
            WorkflowStartInputKind.Number => decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _)
                    ? value
                    : throw new InvalidOperationException(
                        $"Workflow start input '{definition.DisplayName}' expects a numeric value."),
            WorkflowStartInputKind.Boolean => bool.TryParse(value, out var boolValue)
                ? boolValue ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant()
                : throw new InvalidOperationException(
                    $"Workflow start input '{definition.DisplayName}' expects a boolean value."),
            WorkflowStartInputKind.Json => value,
            WorkflowStartInputKind.MarkdownDocument => throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' is a document and cannot be stored as a parameter."),
            _ => throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' uses an unsupported input kind '{definition.Kind}'.")
        };
    }

    private static string MapParameterValueKind(WorkflowStartInputKind kind) =>
        kind switch
        {
            WorkflowStartInputKind.Text => "text",
            WorkflowStartInputKind.Number => "number",
            WorkflowStartInputKind.Boolean => "boolean",
            WorkflowStartInputKind.Json => "json",
            WorkflowStartInputKind.MarkdownDocument => throw new InvalidOperationException(
                "Document inputs must be stored as task session documents."),
            _ => throw new InvalidOperationException(
                $"Unsupported workflow start input kind '{kind}'.")
        };

    private static string? TryGetAgentExecutorId(AIAgent agent)
    {
        if (GetDescriptiveIdMethod.Value?.Invoke(null, [agent]) is string descriptiveId &&
            !string.IsNullOrWhiteSpace(descriptiveId))
        {
            return descriptiveId;
        }

        return TryGetStringProperty(agent, "Id") ?? TryGetStringProperty(agent, "Name");
    }

    private static string? TryGetStringProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(string))
        {
            return null;
        }

        return property.GetValue(value) as string;
    }

    private async Task ValidateResolvedAgentsAsync(
        IReadOnlyList<ResolvedChatAgent> resolvedAgents,
        CancellationToken cancellationToken)
    {
        foreach (var resolvedAgent in resolvedAgents)
        {
            if (resolvedAgent.Model.ServerId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Server is not resolved for agent '{resolvedAgent.Agent.AgentName}'.");
            }

            if (string.IsNullOrWhiteSpace(resolvedAgent.Model.ModelName))
            {
                throw new InvalidOperationException(
                    $"Model is not resolved for agent '{resolvedAgent.Agent.AgentName}'.");
            }

            await modelCapabilityService.EnsureModelSupportedByServerAsync(
                resolvedAgent.Model,
                cancellationToken);

            if (resolvedAgent.Agent.McpServerBindings.Count == 0)
            {
                continue;
            }

            var supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
                resolvedAgent.Model,
                cancellationToken);
            if (!supportsFunctions)
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{resolvedAgent.Agent.AgentName}' requires a model with function calling.");
            }
        }
    }

    private static ResolvedChatAgent BindTaskSession(ResolvedChatAgent source, string sessionId)
    {
        var runtimeAgent = source.Agent.Clone();

        foreach (var binding in runtimeAgent.McpServerBindings)
        {
            if (!string.Equals(binding.ServerName, BuiltInTaskSessionMcpServerTools.Descriptor.Name, StringComparison.OrdinalIgnoreCase) &&
                binding.ServerId != BuiltInTaskSessionMcpServerTools.Descriptor.Id)
            {
                continue;
            }

            binding.Parameters[TaskSessionStore.SessionIdParameter] = sessionId;
        }

        return new ResolvedChatAgent(runtimeAgent, source.Model);
    }
}

public sealed record OrchestrationWorkflowSessionBootstrapResult(
    string TaskSessionId,
    OrchestrationWorkflowSessionStartRequest Request,
    IReadOnlyList<OrchestrationWorkflowRuntimeAgentRegistration> RuntimeAgents);

public sealed record OrchestrationWorkflowRuntimeAgentRegistration(
    string AgentId,
    string AgentName,
    AIAgent RuntimeAgent,
    string? ExecutorId);
