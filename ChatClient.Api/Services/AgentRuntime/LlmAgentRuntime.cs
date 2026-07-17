using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class LlmAgentRuntimeFactory(
    IAgentTemplateService agentTemplateService,
    IChatEngineOrchestrator orchestrator,
    ILogger<LlmAgentRuntimeFactory> logger) : ILlmAgentRuntimeFactory, IInlineLlmAgentRuntimeFactory
{
    public async Task<IAgentRuntime> CreateAsync(
        string agentId,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Guid.TryParse(agentId, out var templateId))
        {
            throw new KeyNotFoundException($"Agent id '{agentId}' is not a valid saved-agent id.");
        }

        var template = await agentTemplateService.GetByIdAsync(templateId);
        if (template is null)
        {
            throw new KeyNotFoundException($"Saved agent '{agentId}' was not found.");
        }

        template = ApplyOverrides(template, context.Overrides);
        var model = ResolveModel(template, context);
        var runtimeAgent = ResolvedChatAgentFactory.Resolve(template, model);
        return new LlmAgentRuntime(
            new AgentRuntimeDescriptor(
                template.Id.ToString("D"),
                template.AgentName,
                template.Summary,
                AgentRuntimeKind.LlmAgent),
            runtimeAgent,
            context.Configuration,
            orchestrator,
            logger);
    }

    public IAgentRuntime Create(
        AgentRuntimeDescriptor descriptor,
        AgentTemplateDefinition agent,
        AgentRuntimeCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var model = ResolveModel(agent, context);
        var runtimeAgent = ResolvedChatAgentFactory.Resolve(agent, model);
        return new LlmAgentRuntime(
            descriptor,
            runtimeAgent,
            context.Configuration,
            orchestrator,
            logger);
    }

    private static ServerModel ResolveModel(
        AgentTemplateDefinition template,
        AgentRuntimeCreationContext context)
    {
        var uiSelection = context.DefaultModel is null
            ? new ServerModelSelection(null, null)
            : new ServerModelSelection(context.DefaultModel.ServerId, context.DefaultModel.ModelName);

        if (ModelSelectionHelper.TryGetEffectiveModel(
                new ServerModelSelection(template.LlmId, template.ModelName),
                uiSelection,
                out var model))
        {
            return model;
        }

        throw new InvalidOperationException(
            $"Model selection for agent '{template.AgentName}' is incomplete.");
    }

    private static AgentTemplateDefinition ApplyOverrides(
        AgentTemplateDefinition template,
        AgentSessionOverrides overrides)
    {
        if (overrides.McpServerBindings is null)
        {
            return template;
        }

        var clone = template.Clone();
        clone.McpServerBindings = overrides.McpServerBindings
            .Select(static binding => binding.Clone())
            .ToList();
        return clone;
    }
}

internal sealed class LlmAgentRuntime(
    AgentRuntimeDescriptor descriptor,
    ResolvedChatAgent agent,
    AppChatConfiguration configuration,
    IChatEngineOrchestrator orchestrator,
    ILogger logger) : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentRunEvent>();
        var producer = ProduceAsync(request, context, channel.Writer, cancellationToken);

        await foreach (var runEvent in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return runEvent;
        }

        await producer;
    }

    private async Task ProduceAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        ChannelWriter<AgentRunEvent> writer,
        CancellationToken cancellationToken)
    {
        var currentUserMessageIndex = FindCurrentUserMessageIndex(request.Messages);
        var userMessage = currentUserMessageIndex >= 0
            ? request.Messages[currentUserMessageIndex]
            : null;
        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
        {
            await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                "invalid_input",
                "At least one non-empty user message is required.",
                false)), cancellationToken);
            writer.TryComplete();
            return;
        }

        var trailingMessages = request.Messages.Skip(currentUserMessageIndex + 1).ToList();
        if (trailingMessages.Count > 0)
        {
            await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                "invalid_input",
                "Messages after the current user message are not supported.",
                false)), cancellationToken);
            writer.TryComplete();
            return;
        }

        var history = BuildHistory(request.Messages.Take(currentUserMessageIndex));
        var files = request.Attachments
            .Select(ToAppChatMessageFile)
            .ToList();
        var orchestrationRequest = new ChatEngineOrchestrationRequest
        {
            Agent = agent.Agent,
            ResolvedModel = agent.Model,
            Configuration = configuration,
            Messages = history,
            UserMessage = userMessage.Content,
            Files = files,
            EnableRagContext = true
        };

        var messageId = Guid.NewGuid().ToString("N");
        var buffer = new List<string>();
        var completedMessages = new List<AgentOutputMessage>();

        try
        {
            await foreach (var chunk in orchestrator.StreamAsync(orchestrationRequest, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (chunk.IsError)
                {
                    await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                        "execution_failed",
                        "Agent execution failed.",
                        true)), cancellationToken);
                    return;
                }

                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    buffer.Add(chunk.Content);
                    await writer.WriteAsync(
                        new AgentTextDelta(messageId, Descriptor.Name, chunk.Content),
                        cancellationToken);
                }

                if (chunk.IsFinal)
                {
                    break;
                }
            }

            var finalContent = string.Concat(buffer).Trim();
            if (string.IsNullOrWhiteSpace(finalContent))
            {
                await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                    "execution_failed",
                    "Agent produced no assistant response.",
                    true)), cancellationToken);
                return;
            }

            var completed = new AgentOutputMessage(Descriptor.Name, finalContent);
            completedMessages.Add(completed);
            await writer.WriteAsync(new AgentMessageCompleted(messageId, completed), cancellationToken);
            await writer.WriteAsync(new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = completed,
                FinalMessageId = messageId,
                Messages = completedMessages
            }), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Saved agent runtime failed. RunId={RunId}, AgentId={AgentId}, AgentName={AgentName}",
                context.RunId,
                Descriptor.Id,
                Descriptor.Name);
            await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                "execution_failed",
                "Agent execution failed.",
                true,
                ex)), CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static int FindCurrentUserMessageIndex(IReadOnlyList<AgentInputMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == AgentMessageRole.User)
            {
                return index;
            }
        }

        return -1;
    }

    private static AppChatMessageFile ToAppChatMessageFile(AgentInputAttachment attachment)
    {
        var data = attachment.Data.Length > 0
            ? attachment.Data
            : Encoding.UTF8.GetBytes(attachment.Content);

        return new AppChatMessageFile(
            attachment.Name,
            data.LongLength,
            attachment.ContentType,
            data);
    }

    private static IReadOnlyList<IAppChatMessage> BuildHistory(IEnumerable<AgentInputMessage> messages)
    {
        var history = new List<IAppChatMessage>();
        foreach (var message in messages)
        {
            var role = message.Role switch
            {
                AgentMessageRole.System => AppChatRole.System,
                AgentMessageRole.User => AppChatRole.User,
                AgentMessageRole.Assistant => AppChatRole.Assistant,
                _ => AppChatRole.User
            };

            history.Add(new AppChatMessage(message.Content, DateTime.Now, role));
        }

        return history;
    }
}
