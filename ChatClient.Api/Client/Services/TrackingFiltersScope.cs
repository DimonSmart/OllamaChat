#pragma warning disable SKEXP0110

using System;
using System.Collections.Generic;

using Microsoft.SemanticKernel.Agents.Runtime.InProcess;

namespace ChatClient.Api.Client.Services;

internal sealed class TrackingFiltersScope : IDisposable
{
    public Dictionary<string, FunctionCallRecordingFilter> Filters { get; } = new();
    private readonly List<Action> _onDispose = new();

    public void Register(string agentName, FunctionCallRecordingFilter filter, Action unregister)
    {
        Filters[agentName] = filter;
        _onDispose.Add(unregister);
    }

    public void Dispose()
    {
        foreach (var action in _onDispose)
        {
            action();
        }

        foreach (var filter in Filters.Values)
        {
            filter.Clear();
        }

        Filters.Clear();
    }
}

#pragma warning restore SKEXP0110
