using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Options;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record HarnessAgentRuntimeDefinition(
    AIAgent Agent,
    LlmServerConfig Server,
    AgenticToolSet ToolSet,
    bool SupportsFunctionCalling);

public sealed class AgenticRuntimeAgentFactory(
    ILlmServerConfigService llmServerConfigService,
    ILlmChatClientFactory llmChatClientFactory,
    IModelCapabilityService modelCapabilityService,
    IAppToolCatalog appToolCatalog,
    IMcpUserInteractionService mcpUserInteractionService,
    IAgenticRagContextService ragContextService,
    IOptions<AgenticToolInvocationPolicyOptions> toolPolicyOptions,
    ILogger<AgenticRuntimeAgentFactory> logger)
{
    internal async Task<HarnessAgentRuntimeDefinition> CreateAsync(
        AgentRunRequest request,
        bool requireFunctionCalling = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var server = await llmServerConfigService.GetByIdAsync(request.ResolvedModel.ServerId);
        if (server is null)
        {
            throw new InvalidOperationException(
                $"Configured LLM server '{request.ResolvedModel.ServerId}' was not found.");
        }

        var chatClient = await llmChatClientFactory.CreateAsync(request.ResolvedModel, cancellationToken);
        bool supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
            request.ResolvedModel,
            cancellationToken);

        var toolRequestContext = BuildToolRequestContext(request);
        var availableTools = supportsFunctions
            ? await appToolCatalog.ListToolsAsync(toolRequestContext, cancellationToken)
            : [];
        var requestedFunctions = ResolveRequestedFunctionNames(request, availableTools);

        if (!supportsFunctions && requestedFunctions.Count > 0)
        {
            if (requireFunctionCalling)
            {
                throw new InvalidOperationException(
                    $"Model '{request.ResolvedModel.ModelName}' does not support function calling required by workflow agent '{request.Agent.AgentName}'.");
            }

            logger.LogInformation(
                "Model {ModelName} for agent {AgentName} does not support function calling. Skipping {FunctionCount} configured tools.",
                request.ResolvedModel.ModelName,
                request.Agent.AgentName,
                requestedFunctions.Count);
        }

        var toolSet = supportsFunctions
            ? AgenticToolSetBuilder.Build(
                requestedFunctions,
                availableTools,
                NormalizeToolPolicy(toolPolicyOptions.Value),
                mcpUserInteractionService,
                logger)
            : AgenticToolSet.Empty;

        if (requestedFunctions.Count > 0 && !toolSet.HasTools)
        {
            logger.LogWarning(
                "No MCP tools matched the configured function set for agent {AgentName}. Requested: [{RequestedFunctions}]",
                request.Agent.AgentName,
                string.Join(", ", requestedFunctions));
        }

        var runtimeAgent = CreateRuntimeAgent(chatClient, request, server, toolSet, ragContextService);
        return new HarnessAgentRuntimeDefinition(
            runtimeAgent,
            server,
            toolSet,
            supportsFunctions);
    }

    private static AIAgent CreateRuntimeAgent(
        IChatClient chatClient,
        AgentRunRequest request,
        LlmServerConfig server,
        AgenticToolSet toolSet,
        IAgenticRagContextService ragContextService)
    {
        // Harness owns the function-invocation loop, session history and compaction.
        // The direct-chat service must not rebuild any of that state from its UI transcript.
        var agentOptions = new HarnessAgentOptions
        {
            Id = string.IsNullOrWhiteSpace(request.Agent.AgentId) ? null : request.Agent.AgentId.Trim(),
            Name = string.IsNullOrWhiteSpace(request.Agent.AgentName) ? null : request.Agent.AgentName.Trim(),
            ChatOptions = new ChatOptions
            {
                Instructions = BuildInstructions(request.Agent),
                Tools = toolSet.Tools.ToList(),
                ModelId = request.ResolvedModel.ModelName,
                Temperature = ResolveTemperature(request.ResolvedModel, request.Agent.Temperature)
            },
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            AIContextProviders = Guid.TryParse(request.Agent.AgentId, out var agentId) && agentId != Guid.Empty
                ? [new AgenticRagContextProvider(agentId, request.ResolvedModel.ServerId, ragContextService)]
                : [],
#pragma warning disable MAAI001
            DisableCompaction = true
#pragma warning restore MAAI001
        };

        if (server.ServerType == ServerType.Ollama &&
            request.Agent.RepeatPenalty is double repeatPenalty)
        {
            agentOptions.ChatOptions.AdditionalProperties ??= [];
            agentOptions.ChatOptions.AdditionalProperties["repeat_penalty"] = repeatPenalty;
        }

        if (toolSet.HasTools)
        {
            agentOptions.ChatOptions.AllowMultipleToolCalls = true;
            agentOptions.ChatOptions.ToolMode = ChatToolMode.Auto;
        }

        return chatClient.AsHarnessAgent(agentOptions);
    }

    private static string? BuildInstructions(AgentExecutionSpec agent)
    {
        var content = agent.Content?.Trim();
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private IReadOnlyList<string> ResolveRequestedFunctionNames(
        AgentRunRequest request,
        IReadOnlyCollection<AppToolDescriptor> availableTools)
    {
        HashSet<string> requested = new(StringComparer.OrdinalIgnoreCase);

        foreach (var function in request.Configuration.Functions)
        {
            if (string.IsNullOrWhiteSpace(function))
            {
                continue;
            }

            requested.Add(function.Trim());
        }

        foreach (var function in McpBindingToolSelectionResolver.ResolveQualifiedToolNames(
                     request.Agent.McpServerBindings,
                     availableTools))
        {
            requested.Add(function);
        }

        if (request.Agent.FunctionSettings.IsAutoSelectEnabled)
        {
            foreach (var tool in availableTools)
            {
                requested.Add(tool.QualifiedName);
            }

            logger.LogInformation(
                "Agent {AgentName} uses AutoSelectCount={AutoSelectCount}; direct Harness registration includes all {ToolCount} tools allowed by current bindings.",
                request.Agent.AgentName,
                request.Agent.FunctionSettings.AutoSelectCount,
                availableTools.Count);
        }

        return requested.ToList();
    }

    private static McpClientRequestContext BuildToolRequestContext(AgentRunRequest request)
    {
        var mergedBindings = McpServerSessionBindingMerger.Merge(
            request.Agent.McpServerBindings,
            request.Configuration.McpServerBindings);

        return mergedBindings.Count == 0
            ? McpClientRequestContext.Empty
            : new McpClientRequestContext(mergedBindings);
    }

    internal static AgenticToolInvocationPolicyOptions NormalizeToolPolicy(AgenticToolInvocationPolicyOptions? policy)
    {
        policy ??= new AgenticToolInvocationPolicyOptions();

        return new AgenticToolInvocationPolicyOptions
        {
            TimeoutSeconds = Math.Max(0, policy.TimeoutSeconds),
            InteractiveTimeoutSeconds = Math.Max(
                Math.Max(0, policy.TimeoutSeconds),
                policy.InteractiveTimeoutSeconds),
            MaxRetries = Math.Max(0, policy.MaxRetries),
            RetryDelayMs = Math.Max(0, policy.RetryDelayMs)
        };
    }

    internal static float? ResolveTemperature(ServerModel model, double? configuredTemperature)
    {
        ArgumentNullException.ThrowIfNull(model);

        return configuredTemperature is double temperature &&
               !model.ModelName.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            ? (float)temperature
            : null;
    }
}
