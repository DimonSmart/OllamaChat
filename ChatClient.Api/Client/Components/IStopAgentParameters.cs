using ChatClient.Shared.Models.StopAgents;

namespace ChatClient.Api.Client.Components;

public interface IStopAgentParameters<out TRealParameterType>
    where TRealParameterType : IStopAgentOptions
{
    TRealParameterType GetOptions();
}
