using System.Collections.Generic;
using System.Linq;

using ChatClient.Shared.LlmAgents;

namespace ChatClient.Api.Services;

/// <summary>
/// Coordinates multiple worker agents using a simple round-robin policy.
/// A manager agent is retained for future, more advanced coordination logic.
/// </summary>
public class MultiLlmAgentCoordinator : ILlmAgentCoordinator
{
    private readonly ILlmAgent _managerAgent;
    private readonly IReadOnlyList<ILlmAgent> _workerAgents;
    private int _currentIndex;
    private readonly int _maxCyclesWithoutUser;

    public MultiLlmAgentCoordinator(ILlmAgent managerAgent, IEnumerable<ILlmAgent> workerAgents, int maxCyclesWithoutUser = 5)
    {
        _managerAgent = managerAgent;
        _workerAgents = workerAgents.ToList();
        _currentIndex = 0;
        _maxCyclesWithoutUser = maxCyclesWithoutUser;
    }

    public ILlmAgent GetNextAgent()
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

