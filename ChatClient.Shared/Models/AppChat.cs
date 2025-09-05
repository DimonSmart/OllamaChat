using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace ChatClient.Shared.Models;

public class AppChat
{
    private readonly Dictionary<string, AgentDescription> _agentsByName = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _agentsByName.Values;
    public IAppChatMessage? FirstUserMessage { get; set; }
    public AppChatConfiguration? InitialConfiguration { get; set; }

    public void SetAgents(IEnumerable<AgentDescription> agents)
    {
        _agentsByName.Clear();
        foreach (var agent in agents)
        {
            _agentsByName[agent.AgentId] = agent;
        }
    }

    public bool TryGetAgent(string agentId, out AgentDescription? description) =>
        _agentsByName.TryGetValue(agentId, out description);

    public void Reset()
    {
        Id = Guid.NewGuid();
        Messages.Clear();
        _agentsByName.Clear();
        FirstUserMessage = null;
        InitialConfiguration = null;
    }
}

