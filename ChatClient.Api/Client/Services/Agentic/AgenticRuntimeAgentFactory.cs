using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgenticRuntimeAgentBuildResult(
    AIAgent Agent,
    LlmServerConfig Server,
    AgenticToolSet ToolSet,
    bool SupportsFunctionCalling);

public sealed class AgenticRuntimeAgentFactory(
    ILlmServerConfigService llmServerConfigService,
    ILlmChatClientFactory llmChatClientFactory,
    IModelCapabilityService modelCapabilityService,
    IAppToolCatalog appToolCatalog,
    KernelService kernelService,
    ILogger<AgenticRuntimeAgentFactory> logger)
{
    private const int MaxLoggedPayloadLength = 4000;

    private static readonly JsonSerializerOptions ToolResultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal async Task<AgenticRuntimeAgentBuildResult> CreateAsync(
        AgentRunRequest request,
        List<FunctionCallRecord>? functionCalls = null,
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
        var requestedFunctions = await ResolveRequestedFunctionNamesAsync(request, availableTools, cancellationToken);

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
            ? AgenticToolSetBuilder.Build(requestedFunctions, availableTools)
            : AgenticToolSet.Empty;

        if (requestedFunctions.Count > 0 && !toolSet.HasTools)
        {
            logger.LogWarning(
                "No MCP tools matched the configured function set for agent {AgentName}. Requested: [{RequestedFunctions}]",
                request.Agent.AgentName,
                string.Join(", ", requestedFunctions));
        }

        var runtimeAgent = CreateRuntimeAgent(chatClient, request, toolSet);
        return new AgenticRuntimeAgentBuildResult(
            runtimeAgent,
            server,
            toolSet,
            supportsFunctions);
    }

    private static AIAgent CreateRuntimeAgent(
        IChatClient chatClient,
        AgentRunRequest request,
        AgenticToolSet toolSet)
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
                Tools = toolSet.Tools.ToList()
            },
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
#pragma warning disable MAAI001
            DisableCompaction = true
#pragma warning restore MAAI001
        };

        return chatClient.AsHarnessAgent(agentOptions);
    }

    private static string? BuildInstructions(AgentExecutionSpec agent)
    {
        var content = agent.Content?.Trim();
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    private async Task<IReadOnlyList<string>> ResolveRequestedFunctionNamesAsync(
        AgentRunRequest request,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        CancellationToken cancellationToken)
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

        try
        {
            var fromAgentSettings = await kernelService.GetFunctionsToRegisterAsync(
                request.Agent.FunctionSettings,
                request.UserMessage,
                availableTools,
                cancellationToken);

            foreach (var function in fromAgentSettings)
            {
                if (string.IsNullOrWhiteSpace(function))
                {
                    continue;
                }

                requested.Add(function.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve function selection for agent {AgentName}. Falling back to explicit configuration only.",
                request.Agent.AgentName);
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

    private static AgenticToolInvocationPolicyOptions NormalizeToolPolicy(AgenticToolInvocationPolicyOptions? policy)
    {
        policy ??= new AgenticToolInvocationPolicyOptions();

        return new AgenticToolInvocationPolicyOptions
        {
            TimeoutSeconds = Math.Max(0, policy.TimeoutSeconds),
            InteractiveTimeoutSeconds = Math.Max(0, policy.InteractiveTimeoutSeconds),
            MaxRetries = Math.Max(0, policy.MaxRetries),
            RetryDelayMs = Math.Max(0, policy.RetryDelayMs)
        };
    }

    private static string SerializeArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        try
        {
            return JsonSerializer.Serialize(arguments, ToolResultJsonOptions);
        }
        catch
        {
            return "{}";
        }
    }

    private static string FormatForLog(string? payload, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "<empty>";
        }

        var singleLine = payload.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..maxLength]}... (truncated, {singleLine.Length} chars)";
    }
}
