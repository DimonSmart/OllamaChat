using ChatClient.Domain.Models;

namespace ChatClient.Application.Helpers;

public static class ModelDiscoveryHelper
{
    public static IReadOnlyList<OllamaModel> FilterByName(IEnumerable<OllamaModel> models, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? models.ToList()
            : models.Where(model => model.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
}
