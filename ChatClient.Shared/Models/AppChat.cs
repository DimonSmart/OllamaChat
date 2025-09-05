using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ChatClient.Shared.Models;

public class AppChat
{
    private readonly Dictionary<string, AgentDescription> _agentsByName = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public string? FirstUserMessage { get; set; }
    public ServerModelSelection? InitialModel { get; set; }

    public IReadOnlyDictionary<string, AgentDescription> AgentsByName => _agentsByName;
    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _agentsByName.Values;

    public void SetAgents(IEnumerable<AgentDescription> agents)
    {
        _agentsByName.Clear();
        foreach (var agent in agents)
        {
            _agentsByName[agent.AgentId] = agent;
        }
    }

    public void Reset()
    {
        Id = Guid.NewGuid();
        Messages.Clear();
        _agentsByName.Clear();
        FirstUserMessage = null;
        InitialModel = null;
    }
}

