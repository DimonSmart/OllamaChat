using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChatClient.Shared.Helpers;

/// <summary>
/// Helper for determining effective server+model pair with selection logging
/// </summary>
public static class ModelSelectionHelper
{
    /// <summary>
    /// Determines effective server+model pair based on priority: configuration > UI selection
    /// </summary>
    /// <param name="configuredModel">Model specified in configuration (agent, settings, etc.)</param>
    /// <param name="uiSelectedModel">Model selected in UI</param>
    /// <param name="context">Context description for logging (e.g., "Agent: Kant", "Embedding", "Default chat")</param>
    /// <param name="logger">Logger for recording selection</param>
    /// <returns>Effective server+model pair</returns>
    public static ServerModel GetEffectiveModel(
        ServerModel? configuredModel,
        ServerModel uiSelectedModel,
        string context,
        ILogger? logger = null)
    {
        var isConfiguredModelValid = IsValidModel(configuredModel);

        ServerModel effectiveModel;
        string source;

        if (isConfiguredModelValid)
        {
            effectiveModel = configuredModel!;
            source = "configuration";
        }
        else
        {
            effectiveModel = uiSelectedModel;
            source = "UI selection";
        }

        logger?.LogDebug("Model selection for {Context}: using {ModelName} on {ServerName} from {Source}",
            context,
            effectiveModel.ModelName,
            GetServerDisplayName(effectiveModel.ServerId),
            source);

        return effectiveModel;
    }

    /// <summary>
    /// Determines effective embedding model based on special configuration or fallback to regular model
    /// </summary>
    /// <param name="embeddingModel">Special embedding model from settings</param>
    /// <param name="defaultModel">Default model</param>
    /// <param name="context">Context description for logging</param>
    /// <param name="logger">Logger for recording selection</param>
    /// <returns>Effective embedding model</returns>
    public static ServerModel GetEffectiveEmbeddingModel(
        ServerModel embeddingModel,
        ServerModel defaultModel,
        string context,
        ILogger? logger = null)
    {
        var isEmbeddingModelValid = IsValidModel(embeddingModel);

        ServerModel effectiveModel;
        string source;

        if (isEmbeddingModelValid)
        {
            effectiveModel = embeddingModel;
            source = "embedding configuration";
        }
        else
        {
            effectiveModel = defaultModel;
            source = "default model fallback";
        }

        logger?.LogDebug("Embedding model selection for {Context}: using {ModelName} on {ServerName} from {Source}",
            context,
            effectiveModel.ModelName,
            GetServerDisplayName(effectiveModel.ServerId),
            source);

        return effectiveModel;
    }



    /// <summary>
    /// Validates model by checking if both server and model name are specified
    /// </summary>
    private static bool IsValidModel(ServerModel? model)
    {
        return model != null &&
               model.ServerId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(model.ModelName);
    }

    private static string GetServerDisplayName(Guid serverId)
    {
        return serverId == Guid.Empty ? "Unknown" : serverId.ToString()[..8];
    }
}
