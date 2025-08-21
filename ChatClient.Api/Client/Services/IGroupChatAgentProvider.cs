using System.Collections.Generic;

namespace ChatClient.Api.Client.Services;

public interface IGroupChatAgentProvider
{
    IEnumerable<string> GetRequiredAgents();
}
