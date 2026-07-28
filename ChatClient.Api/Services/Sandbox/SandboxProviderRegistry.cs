using ChatClient.Application.Services.Sandbox;

namespace ChatClient.Api.Services.Sandbox;

public sealed class SandboxProviderRegistry : ISandboxProviderRegistry
{
    private readonly IReadOnlyDictionary<string, ISandboxProvider> _providers;

    public SandboxProviderRegistry(IEnumerable<ISandboxProvider> providers)
    {
        Dictionary<string, ISandboxProvider> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Type))
            {
                throw new InvalidOperationException("Sandbox provider type cannot be empty.");
            }

            if (!map.TryAdd(provider.Type.Trim(), provider))
            {
                throw new InvalidOperationException($"Duplicate sandbox provider type '{provider.Type}'.");
            }
        }

        _providers = map;
    }

    public IReadOnlyList<SandboxProviderDescriptor> GetProviders() =>
        _providers.Values
            .OrderBy(static provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(static provider => new SandboxProviderDescriptor(
                provider.Type,
                provider.DisplayName,
                provider.DefaultConfiguration))
            .ToList();

    public ISandboxProvider GetRequired(string providerType) =>
        TryGet(providerType, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Sandbox provider '{providerType}' was not found.");

    public bool TryGet(string providerType, out ISandboxProvider provider) =>
        _providers.TryGetValue(providerType?.Trim() ?? string.Empty, out provider!);
}
