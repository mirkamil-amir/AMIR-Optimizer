using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;

namespace PortfolioOptimizer.Infrastructure.MarketData;

public sealed class MarketDataProviderRegistry(IEnumerable<IMarketDataProvider> providers) : IMarketDataProviderRegistry
{
    private readonly Dictionary<string, IMarketDataProvider> _providers =
        providers.ToDictionary(p => p.Descriptor.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderDescriptor> Providers => _providers.Values.Select(p => p.Descriptor).ToList();

    public IMarketDataProvider Get(string name)
    {
        if (!_providers.TryGetValue(name, out var provider))
            throw new ValidationException("provider", $"Unknown market data provider '{name}'. Available: {string.Join(", ", _providers.Keys)}.");
        if (!provider.Descriptor.IsConfigured)
        {
            throw new ValidationException("provider", provider.Descriptor.RequiresApiKey
                ? $"Provider '{provider.Descriptor.Name}' requires an API key. Set MarketData__{provider.Descriptor.Name}__ApiKey on the server."
                : $"Provider '{provider.Descriptor.Name}' is disabled in configuration.");
        }
        return provider;
    }
}
