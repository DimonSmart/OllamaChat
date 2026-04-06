using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Application.Services.Agentic;

public sealed class AgentTemplateBuilder
{
    private readonly AgentTemplateDefinition _template;

    private AgentTemplateBuilder(AgentTemplateDefinition template)
    {
        _template = template;
    }

    public static AgentTemplateBuilder New(string agentName, string? shortName = null)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name is required.", nameof(agentName));
        }

        return new AgentTemplateBuilder(new AgentTemplateDefinition
        {
            AgentName = agentName.Trim(),
            ShortName = string.IsNullOrWhiteSpace(shortName) ? null : shortName.Trim()
        });
    }

    public static AgentTemplateBuilder From(AgentTemplateDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AgentTemplateBuilder(source.Clone());
    }

    public AgentTemplateBuilder WithId(Guid id)
    {
        _template.Id = id;
        return this;
    }

    public AgentTemplateBuilder Named(string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name is required.", nameof(agentName));
        }

        _template.AgentName = agentName.Trim();
        return this;
    }

    public AgentTemplateBuilder WithSummary(string? summary)
    {
        _template.Summary = summary?.Trim() ?? string.Empty;
        return this;
    }

    public AgentTemplateBuilder Alias(string? shortName)
    {
        _template.ShortName = string.IsNullOrWhiteSpace(shortName) ? null : shortName.Trim();
        return this;
    }

    public AgentTemplateBuilder WithAvatarText(string? avatarText)
    {
        _template.AvatarText = string.IsNullOrWhiteSpace(avatarText) ? null : avatarText.Trim();
        return this;
    }

    public AgentTemplateBuilder WithInstructions(string content)
    {
        _template.Content = content?.Trim() ?? string.Empty;
        return this;
    }

    public AgentTemplateBuilder WithDefaultModel(Guid serverId, string modelName)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id is required.", nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name is required.", nameof(modelName));
        }

        _template.LlmId = serverId;
        _template.ModelName = modelName.Trim();
        return this;
    }

    public AgentTemplateBuilder WithoutDefaultModel()
    {
        _template.LlmId = null;
        _template.ModelName = null;
        return this;
    }

    public AgentTemplateBuilder WithTemperature(double? temperature)
    {
        _template.Temperature = temperature;
        return this;
    }

    public AgentTemplateBuilder WithRepeatPenalty(double? repeatPenalty)
    {
        _template.RepeatPenalty = repeatPenalty;
        return this;
    }

    public AgentTemplateBuilder AutoSelectTools(int autoSelectCount)
    {
        if (autoSelectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(autoSelectCount), "Auto-select count cannot be negative.");
        }

        _template.FunctionSettings.AutoSelectCount = autoSelectCount;
        return this;
    }

    public AgentTemplateBuilder ConfigureExecution(Action<AgentExecutionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AgentExecutionBuilder(_template.ExecutionSettings);
        configure(builder);
        _template.ExecutionSettings = builder.Build();
        return this;
    }

    public AgentTemplateBuilder ClearBindings()
    {
        _template.McpServerBindings.Clear();
        return this;
    }

    public AgentTemplateBuilder WithoutBinding(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return this;
        }

        _template.McpServerBindings.RemoveAll(binding =>
            string.Equals(binding.ServerName, serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public AgentTemplateBuilder WithBinding(string serverName, Action<AgentMcpBindingBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("Server name is required.", nameof(serverName));
        }

        ArgumentNullException.ThrowIfNull(configure);

        var existing = _template.McpServerBindings.FirstOrDefault(binding =>
            string.Equals(binding.ServerName, serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        var builder = new AgentMcpBindingBuilder(existing)
            .ForServer(serverName.Trim());
        configure(builder);
        UpsertBinding(_template.McpServerBindings, builder.Build());
        return this;
    }

    public AgentTemplateBuilder WithBinding(Guid serverId, string serverName, Action<AgentMcpBindingBuilder> configure)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id is required.", nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("Server name is required.", nameof(serverName));
        }

        ArgumentNullException.ThrowIfNull(configure);

        var existing = _template.McpServerBindings.FirstOrDefault(binding =>
            binding.ServerId == serverId ||
            string.Equals(binding.ServerName, serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        var builder = new AgentMcpBindingBuilder(existing)
            .ForServer(serverId, serverName.Trim());
        configure(builder);
        UpsertBinding(_template.McpServerBindings, builder.Build());
        return this;
    }

    public AgentTemplateDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(_template.AgentName))
        {
            throw new InvalidOperationException("Agent name is required.");
        }

        return _template.Clone();
    }

    private static void UpsertBinding(List<McpServerSessionBinding> bindings, McpServerSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(binding);

        var bindingKey = binding.GetBindingKey();
        var identityKey = binding.GetIdentityKey();

        bindings.RemoveAll(existing =>
            string.Equals(existing.GetBindingKey(), bindingKey, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(identityKey) &&
             string.Equals(existing.GetIdentityKey(), identityKey, StringComparison.OrdinalIgnoreCase)));

        bindings.Add(binding);
    }
}

public sealed class AgentExecutionBuilder
{
    private readonly AgentExecutionSettings _settings;

    internal AgentExecutionBuilder(AgentExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = new AgentExecutionSettings
        {
            MaxToolCalls = settings.MaxToolCalls,
            HistoryCompaction = new AgentHistoryCompactionSettings
            {
                Enabled = settings.HistoryCompaction.Enabled,
                Mode = settings.HistoryCompaction.Mode,
                KeepLastToolPairs = settings.HistoryCompaction.KeepLastToolPairs,
                ToolNames = settings.HistoryCompaction.ToolNames.ToList()
            }
        };
    }

    public AgentExecutionBuilder WithMaxToolCalls(int? maxToolCalls)
    {
        if (maxToolCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "Max tool calls cannot be negative.");
        }

        _settings.MaxToolCalls = maxToolCalls;
        return this;
    }

    public AgentExecutionBuilder WithoutMaxToolCalls()
    {
        _settings.MaxToolCalls = null;
        return this;
    }

    public AgentExecutionBuilder DisableHistoryCompaction()
    {
        _settings.HistoryCompaction = new AgentHistoryCompactionSettings
        {
            Enabled = false,
            Mode = AgentHistoryCompactionModes.None,
            KeepLastToolPairs = 0,
            ToolNames = []
        };
        return this;
    }

    public AgentExecutionBuilder UseToolWindowCompaction(int keepLastToolPairs, params string[] toolNames)
    {
        if (keepLastToolPairs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLastToolPairs), "Keep-last count must be greater than zero.");
        }

        _settings.HistoryCompaction = new AgentHistoryCompactionSettings
        {
            Enabled = true,
            Mode = AgentHistoryCompactionModes.ToolWindow,
            KeepLastToolPairs = keepLastToolPairs,
            ToolNames = NormalizeValues(toolNames)
        };
        return this;
    }

    internal AgentExecutionSettings Build()
    {
        return new AgentExecutionSettings
        {
            MaxToolCalls = _settings.MaxToolCalls,
            HistoryCompaction = new AgentHistoryCompactionSettings
            {
                Enabled = _settings.HistoryCompaction.Enabled,
                Mode = _settings.HistoryCompaction.Mode,
                KeepLastToolPairs = _settings.HistoryCompaction.KeepLastToolPairs,
                ToolNames = _settings.HistoryCompaction.ToolNames.ToList()
            }
        };
    }

    private static List<string> NormalizeValues(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AgentMcpBindingBuilder
{
    private readonly McpServerSessionBinding _binding;

    internal AgentMcpBindingBuilder(McpServerSessionBinding? source = null)
    {
        _binding = source?.Clone() ?? new McpServerSessionBinding();
    }

    public AgentMcpBindingBuilder ForServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("Server name is required.", nameof(serverName));
        }

        _binding.ServerName = serverName.Trim();
        return this;
    }

    public AgentMcpBindingBuilder ForServer(Guid serverId, string serverName)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id is required.", nameof(serverId));
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("Server name is required.", nameof(serverName));
        }

        _binding.ServerId = serverId;
        _binding.ServerName = serverName.Trim();
        return this;
    }

    public AgentMcpBindingBuilder DisplayAs(string? displayName)
    {
        _binding.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        return this;
    }

    public AgentMcpBindingBuilder Enabled(bool enabled = true)
    {
        _binding.Enabled = enabled;
        return this;
    }

    public AgentMcpBindingBuilder SelectAllTools()
    {
        _binding.SelectAllTools = true;
        _binding.SelectedTools.Clear();
        return this;
    }

    public AgentMcpBindingBuilder OnlyTools(params string[] toolNames)
    {
        _binding.SelectAllTools = false;
        _binding.SelectedTools = NormalizeValues(toolNames);
        return this;
    }

    public AgentMcpBindingBuilder WithRoots(params string[] roots)
    {
        _binding.Roots = NormalizeValues(roots);
        return this;
    }

    public AgentMcpBindingBuilder WithParameter(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Parameter key is required.", nameof(key));
        }

        var normalizedKey = key.Trim();
        if (value is null)
        {
            _binding.Parameters.Remove(normalizedKey);
        }
        else
        {
            _binding.Parameters[normalizedKey] = value;
        }

        return this;
    }

    internal McpServerSessionBinding Build()
    {
        if (!_binding.HasIdentity)
        {
            throw new InvalidOperationException("MCP binding must specify a server id or server name.");
        }

        return _binding.Clone();
    }

    private static List<string> NormalizeValues(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AgentRunBuilder
{
    private readonly AgentExecutionSpec _agent;
    private readonly HashSet<string> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<McpServerSessionBinding> _mcpServerBindings = [];
    private readonly List<AgentRunConversationMessage> _conversation = [];
    private ServerModel? _resolvedModel;
    private string _userMessage = string.Empty;

    private AgentRunBuilder(AgentExecutionSpec agent)
    {
        _agent = agent.Clone();
    }

    public static AgentRunBuilder For(AgentExecutionSpec agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new AgentRunBuilder(agent);
    }

    public AgentRunBuilder UsingModel(ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _resolvedModel = new ServerModel(model.ServerId, model.ModelName);
        return this;
    }

    public AgentRunBuilder UsingModel(Guid serverId, string modelName)
    {
        return UsingModel(new ServerModel(serverId, modelName));
    }

    public AgentRunBuilder UsingDefaultModel()
    {
        if (_agent.LlmId is not Guid serverId || serverId == Guid.Empty)
        {
            throw new InvalidOperationException($"Agent '{_agent.AgentName}' does not have a default server configured.");
        }

        if (string.IsNullOrWhiteSpace(_agent.ModelName))
        {
            throw new InvalidOperationException($"Agent '{_agent.AgentName}' does not have a default model configured.");
        }

        _resolvedModel = new ServerModel(serverId, _agent.ModelName.Trim());
        return this;
    }

    public AgentRunBuilder WithConfiguration(AppChatConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        WithFunctions(configuration.Functions);
        WithBindings(configuration.McpServerBindings);
        return this;
    }

    public AgentRunBuilder WithFunctions(params string[] functions)
    {
        return WithFunctions((IEnumerable<string>)functions);
    }

    public AgentRunBuilder WithFunctions(IEnumerable<string> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);

        foreach (var function in functions)
        {
            if (string.IsNullOrWhiteSpace(function))
            {
                continue;
            }

            _functions.Add(function.Trim());
        }

        return this;
    }

    public AgentRunBuilder ClearFunctions()
    {
        _functions.Clear();
        return this;
    }

    public AgentRunBuilder WithBindings(IEnumerable<McpServerSessionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                continue;
            }

            UpsertBinding(_mcpServerBindings, binding.Clone());
        }

        return this;
    }

    public AgentRunBuilder ClearBindings()
    {
        _mcpServerBindings.Clear();
        return this;
    }

    public AgentRunBuilder WithBinding(string serverName, Action<AgentMcpBindingBuilder> configure)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("Server name is required.", nameof(serverName));
        }

        ArgumentNullException.ThrowIfNull(configure);

        var existing = _mcpServerBindings.FirstOrDefault(binding =>
            string.Equals(binding.ServerName, serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        var builder = new AgentMcpBindingBuilder(existing)
            .ForServer(serverName.Trim());
        configure(builder);
        UpsertBinding(_mcpServerBindings, builder.Build());
        return this;
    }

    public AgentRunBuilder WithConversation(IEnumerable<ChatMessage> conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        _conversation.Clear();
        _conversation.AddRange(conversation.Select(static message =>
            new AgentRunConversationMessage(message.Role.ToAppChatRole(), message.Text ?? string.Empty)));
        return this;
    }

    public AgentRunBuilder AddMessage(ChatRole role, string text)
    {
        _conversation.Add(new AgentRunConversationMessage(role.ToAppChatRole(), text));
        return this;
    }

    public AgentRunBuilder WithUserMessage(string userMessage)
    {
        _userMessage = userMessage?.Trim() ?? string.Empty;
        return this;
    }

    public AgentRunRequest Build()
    {
        if (_resolvedModel is null)
        {
            throw new InvalidOperationException($"Resolved model is not configured for agent '{_agent.AgentName}'.");
        }

        var runtimeAgent = _agent.Clone();
        runtimeAgent.LlmId = _resolvedModel.ServerId;
        runtimeAgent.ModelName = _resolvedModel.ModelName;

        return new AgentRunRequest
        {
            Agent = runtimeAgent,
            ResolvedModel = new ServerModel(_resolvedModel.ServerId, _resolvedModel.ModelName),
            Configuration = new AppChatConfiguration(
                _resolvedModel.ModelName,
                _functions.ToArray(),
                _mcpServerBindings.Select(static binding => binding.Clone()).ToArray()),
            Conversation = _conversation.ToArray(),
            UserMessage = _userMessage
        };
    }

    private static void UpsertBinding(List<McpServerSessionBinding> bindings, McpServerSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(binding);

        var bindingKey = binding.GetBindingKey();
        var identityKey = binding.GetIdentityKey();

        bindings.RemoveAll(existing =>
            string.Equals(existing.GetBindingKey(), bindingKey, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(identityKey) &&
             string.Equals(existing.GetIdentityKey(), identityKey, StringComparison.OrdinalIgnoreCase)));

        bindings.Add(binding);
    }
}

public static class AgentTemplateFluentExtensions
{
    public static AgentRunBuilder ForRun(this AgentExecutionSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AgentRunBuilder.For(source);
    }

    public static AgentRunBuilder ForRun(this AgentTemplateDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AgentRunBuilder.For(AgentExecutionSpecFactory.FromTemplate(source));
    }
}
