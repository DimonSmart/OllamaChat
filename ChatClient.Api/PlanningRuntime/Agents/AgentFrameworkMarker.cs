using Microsoft.Agents.Core.Models;

namespace ChatClient.Api.PlanningRuntime.Agents;

// This marker keeps a direct compile-time reference to Microsoft Agent Framework package.
public static class AgentFrameworkMarker
{
    public static Activity CreateDiagnosticActivity(string text) =>
        new()
        {
            Type = ActivityTypes.Message,
            Text = text
        };
}

