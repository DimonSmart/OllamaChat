using System.Collections.Generic;
using System.Linq;

using ChatClient.Shared.Agents;

namespace ChatClient.Api.Services;

/// <summary>
/// Coordinates multiple worker agents using a simple round-robin policy.
/// A manager agent is retained for future, more advanced coordination logic.
/// </summary>
public class MultiAgentCoordinator : IAgentCoordinator
{
    private readonly IAgent _managerAgent;
    private readonly IReadOnlyList<IAgent> _workerAgents;
    private int _currentIndex;
    private readonly int _maxCyclesWithoutUser;

    public MultiAgentCoordinator(IAgent managerAgent, IEnumerable<IAgent> workerAgents, int maxCyclesWithoutUser = 5)
    {
        _managerAgent = managerAgent;
        _workerAgents = workerAgents.ToList();
        _currentIndex = 0;
        _maxCyclesWithoutUser = maxCyclesWithoutUser;
    }

    public IAgent GetNextAgent()
    {
        if (_workerAgents.Count == 0)
        {
            return _managerAgent;
        }

        var agent = _workerAgents[_currentIndex];
        _currentIndex = (_currentIndex + 1) % _workerAgents.Count;
        return agent;
    }

    public bool ShouldContinueConversation(int cycleCount) => cycleCount < _maxCyclesWithoutUser;
}

