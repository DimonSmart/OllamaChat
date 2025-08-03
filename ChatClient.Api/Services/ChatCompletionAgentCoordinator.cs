using System.Collections.Generic;
using System.Linq;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

/// <summary>
/// Simple round-robin coordinator for <see cref="SystemPrompt"/> based agents.
/// Determines which agent should respond next and controls auto-continue cycles.
/// </summary>
public class ChatCompletionAgentCoordinator
{
    private readonly IReadOnlyList<SystemPrompt> _agents;
    private int _currentIndex;
    private readonly int _maxCyclesWithoutUser;

    public ChatCompletionAgentCoordinator(IEnumerable<SystemPrompt> agents, int maxCyclesWithoutUser = 5)
    {
        _agents = agents.ToList();
        _currentIndex = 0;
        _maxCyclesWithoutUser = maxCyclesWithoutUser;
    }

    public SystemPrompt GetNextAgentPrompt()
    {
        var agent = _agents[_currentIndex];
        _currentIndex = (_currentIndex + 1) % _agents.Count;
        return agent;
    }

    public bool ShouldContinueConversation(int cycleCount) => cycleCount < _maxCyclesWithoutUser;
}
