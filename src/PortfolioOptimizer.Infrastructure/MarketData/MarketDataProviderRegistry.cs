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
            throw new ValidationException("provider", "Unknown data source.");
        if (!provider.Descriptor.IsConfigured)
        {
            throw new ValidationException("provider", provider.Descriptor.RequiresApiKey
                ? "The selected data source requires an API key that is not configured on the server."
                : "The selected data source is disabled in configuration.");
        }
        return provider;
    }
}
