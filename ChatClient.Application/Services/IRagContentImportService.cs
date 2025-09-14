using System;

namespace ChatClient.Application.Services;

public interface IRagContentImportService
{
    Task AddContentAsync(Guid agentId, string content, string sourceName);
}
