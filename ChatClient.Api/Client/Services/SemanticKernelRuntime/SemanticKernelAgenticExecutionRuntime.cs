using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Client.Services.Whiteboard;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services.SemanticKernelRuntime;

public sealed class SemanticKernelAgenticExecutionRuntime(
    KernelService kernelService,
    IChatHistoryReducer reducer,
    IOllamaKernelService ollamaKernelService,
    IOpenAIClientService openAIClientService,
    IModelCapabilityService modelCapabilityService,
    IUserSettingsService userSettingsService,
    ILlmServerConfigService llmServerConfigService,
    IAgenticRagContextService ragContextService,
    IOptions<ChatEngineOptions> chatEngineOptions,
    ILogger<SemanticKernelToolInvocationPolicyFilter> toolFilterLogger,
    ILogger<SemanticKernelRagRetrievalKernelPlugin> ragPluginLogger,
    ILogger<SemanticKernelAgenticExecutionRuntime> logger) : IAgenticExecutionRuntime
{
    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgenticExecutionRuntimeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (agent, toolPolicyFilter) = await CreateAgentAsync(request, cancellationToken);
        var conversation = request.Conversation.ToList();

        await foreach (var update in agent.RunStreamingAsync(conversation, thread: null, options: null, cancellationToken).WithCancellation(cancellationToken))
        {
            if (string.IsNullOrEmpty(update.Text))
            {
                continue;
            }

            var agentName = string.IsNullOrWhiteSpace(update.AuthorName)
                ? request.Agent.AgentName
                : update.AuthorName!;

            yield return new ChatEngineStreamChunk(agentName, update.Text);
        }

        yield return new ChatEngineStreamChunk(
            request.Agent.AgentName,
            string.Empty,
            IsFinal: true,
            FunctionCalls: toolPolicyFilter.Records);
    }

    private async Task<(AIAgent Agent, SemanticKernelToolInvocationPolicyFilter ToolPolicyFilter)> CreateAgentAsync(
        AgenticExecutionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var agentDescription = request.Agent;
        string modelName = agentDescription.ModelName
            ?? request.Configuration.ModelName
            ?? throw new InvalidOperationException($"Agent '{agentDescription.AgentName}' model name is not set.");

        var serverModel = new ServerModel(agentDescription.LlmId ?? Guid.Empty, modelName);
        bool supportsFunctionCalling = await modelCapabilityService.SupportsFunctionCallingAsync(serverModel, cancellationToken);

        var functionsToRegister = supportsFunctionCalling
            ? await kernelService.GetFunctionsToRegisterAsync(
                agentDescription.FunctionSettings,
                request.UserMessage,
                cancellationToken)
            : [];

        if (!supportsFunctionCalling)
        {
            logger.LogInformation(
                "Model {ModelName} for agent {AgentName} does not support function calling. Running without MCP/RAG/whiteboard tools.",
                modelName,
                agentDescription.AgentName);
        }

        var settings = new PromptExecutionSettings
        {
            ModelId = modelName
        };
        if (supportsFunctionCalling)
        {
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true);
        }

        if (agentDescription.Temperature.HasValue)
        {
            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData["temperature"] = agentDescription.Temperature.Value;
        }

        if (agentDescription.RepeatPenalty.HasValue)
        {
            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData["repeat_penalty"] = agentDescription.RepeatPenalty.Value;
        }

        var toolPolicyFilter = new SemanticKernelToolInvocationPolicyFilter(chatEngineOptions.Value.ToolPolicy, toolFilterLogger);
        var kernel = await CreateKernelAsync(
            serverModel,
            functionsToRegister,
            agentDescription.AgentName,
            agentDescription.Id,
            agentDescription.LlmId,
            request.Configuration,
            supportsFunctionCalling,
            toolPolicyFilter,
            cancellationToken);

        var chatAgent = new ChatCompletionAgent
        {
            Name = agentDescription.AgentId,
            Description = agentDescription.AgentName,
            Instructions = agentDescription.Content,
            Kernel = kernel,
            Arguments = new KernelArguments(settings),
            HistoryReducer = reducer
        };

        return (chatAgent.AsAIAgent(), toolPolicyFilter);
    }

    private async Task<Kernel> CreateKernelAsync(
        ServerModel serverModel,
        IEnumerable<string>? functionsToRegister,
        string agentName,
        Guid agentId,
        Guid? ragServerId,
        AppChatConfiguration chatConfiguration,
        bool supportsFunctionCalling,
        SemanticKernelToolInvocationPolicyFilter toolPolicyFilter,
        CancellationToken cancellationToken = default)
    {
        var builder = Kernel.CreateBuilder();

        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));

        var serverType = await LlmServerConfigHelper.GetServerTypeAsync(
            llmServerConfigService,
            userSettingsService,
            serverModel.ServerId);

        IChatClient rawClient = serverType == ServerType.ChatGpt
            ? (await openAIClientService.GetClientAsync(serverModel, cancellationToken)).AsChatClient()
            : (await ollamaKernelService.GetClientAsync(serverModel.ServerId)).AsChatClient();

        IChatClient fcClient = supportsFunctionCalling
            ? rawClient.AsBuilder().UseKernelFunctionInvocation().Build()
            : rawClient;

        builder.Services.AddSingleton<IChatClient>(fcClient);
        builder.Services.AddSingleton<IChatCompletionService>(_ =>
            new AppForceLastUserChatCompletionService(fcClient.AsChatCompletionService(), reducer));

        var kernel = builder.Build();
        if (supportsFunctionCalling)
        {
            kernel.FunctionInvocationFilters.Add(toolPolicyFilter);
        }

        if (chatConfiguration.UseWhiteboard && supportsFunctionCalling)
        {
            var whiteboard = new WhiteboardState();
            var whiteboardPlugin = new WhiteboardKernelPlugin(whiteboard, _ => Task.CompletedTask);
            kernel.Plugins.AddFromObject(whiteboardPlugin, "whiteboard");
        }

        if (supportsFunctionCalling)
        {
            var ragPlugin = new SemanticKernelRagRetrievalKernelPlugin(agentId, ragServerId, ragContextService, ragPluginLogger);
            kernel.Plugins.AddFromObject(ragPlugin, "rag");
        }

        if (supportsFunctionCalling && functionsToRegister != null && functionsToRegister.Any())
        {
            await kernelService.RegisterMcpToolsPublicAsync(kernel, functionsToRegister, cancellationToken);
            logger.LogInformation("Agentic orchestrator registered tools for {AgentName}: [{Functions}]",
                agentName,
                string.Join(", ", functionsToRegister));
        }

        return kernel;
    }
}
