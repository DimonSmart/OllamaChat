using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ChatClient.Domain.Models;

public class AppChat
{
    private readonly Dictionary<string, AgentExecutionSpec> _agentsById = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public string? FirstUserMessage { get; set; }
    public ServerModelSelection? InitialModel { get; set; }

    public IReadOnlyDictionary<string, AgentExecutionSpec> AgentsById => _agentsById;
    public IReadOnlyCollection<AgentExecutionSpec> Agents => _agentsById.Values;

    public void SetAgents(IEnumerable<AgentExecutionSpec> agents)
    {
        _agentsById.Clear();
        foreach (var agent in agents)
        {
            _agentsById[agent.AgentId] = agent;
        }
    }

    public void Reset()
    {
        Id = Guid.NewGuid();
        Messages.Clear();
        _agentsById.Clear();
        FirstUserMessage = null;
        InitialModel = null;
    }
}

