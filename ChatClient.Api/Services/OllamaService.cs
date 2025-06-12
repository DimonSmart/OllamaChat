#pragma warning disable SKEXP0070
using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;

using OllamaSharp;

namespace ChatClient.Api.Services;

public class OllamaService(
    IConfiguration configuration,
    ILogger<OllamaService> logger)
{
    private OllamaApiClient? _ollamaClient;

    private OllamaApiClient GetOllamaClient()
    {
        if (_ollamaClient == null)
        {
            var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
            _ollamaClient = new OllamaApiClient(new Uri(baseUrl));
        }
        return _ollamaClient;
    }


    /// <summary>
    /// Gets the list of available Ollama models using Semantic Kernel's ListLocalModelsAsync method.
    /// This approach leverages the same OllamaApiClient instance used by the Kernel for consistency.
    /// </summary>
    /// <returns>List of OllamaModel objects with vision capability detection</returns>
    public async Task<List<OllamaModel>> GetModelsAsync()
    {
        try
        {
            var ollamaClient = GetOllamaClient();
            var localModels = await ollamaClient.ListLocalModelsAsync(); var result = new List<OllamaModel>();
            foreach (var model in localModels)
            {
                var ollamaModel = new OllamaModel
                {
                    Name = model.Name,
                    ModifiedAt = model.ModifiedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Size = model.Size,
                    Digest = model.Digest,
                    // Detect vision capability based on "clip" family in model details
                    SupportsImages = model.Details?.Families?.Contains("clip") == true
                };
                result.Add(ollamaModel);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve Ollama models: {Message}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Gets the OllamaApiClient for use in other services (e.g., KernelService)
    /// </summary>
    public OllamaApiClient GetClient() => GetOllamaClient();
}
