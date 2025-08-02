using System;
using System.Collections.Generic;
using System.Linq;
using ChatClient.Shared.LlmAgents;

namespace ChatClient.Api.Services;

/// <summary>
/// Coordinates a fixed list of agents so each one responds exactly once
/// before control returns to the user.
/// </summary>
public class SingleRoundLlmAgentCoordinator : ILlmAgentCoordinator
{
    private readonly IReadOnlyList<ILlmAgent> _agents;
    private int _currentIndex;

    public SingleRoundLlmAgentCoordinator(IEnumerable<ILlmAgent> agents)
    {
        _agents = agents.ToList();
        _currentIndex = 0;
    }

    public ILlmAgent GetNextAgent()
    {
        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("No agents available");
        }
        var agent = _agents[_currentIndex];
        _currentIndex = (_currentIndex + 1) % _agents.Count;
        return agent;
    }

    public bool ShouldContinueConversation(int cycleCount) => cycleCount < _agents.Count;
}
