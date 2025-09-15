using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System;

namespace ChatClient.Api.Services.Rag;

public class RagContentImportService(IRagFileService fileService) : IRagContentImportService
{
    private readonly IRagFileService _fileService = fileService;

    public Task AddContentAsync(Guid agentId, string content, string sourceName)
        => _fileService.AddOrUpdateFileAsync(agentId, new RagFile { FileName = sourceName, Content = content });
}
