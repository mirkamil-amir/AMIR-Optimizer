using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Settings;

public sealed class SettingsService(
    IApplicationDbContext db,
    IMarketDataProviderRegistry providers,
    IOptions<MarketDataOptions> options,
    TimeProvider clock)
{
    public async Task<SettingsDto> GetAsync(CancellationToken ct)
    {
        var s = await db.Settings.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return ToDto(s);
    }

    public async Task<SettingsDto> UpdateAsync(UpdateSettingsRequest request, CancellationToken ct)
    {
        var provider = providers.Get(request.ActiveProvider); // throws ValidationException if unknown/unconfigured
        var s = await db.Settings.SingleAsync(x => x.Id == 1, ct);
        s.ActiveProvider = provider.Descriptor.Name;
        s.DefaultCurrency = request.DefaultCurrency.ToUpperInvariant();
        s.DefaultRiskFreeRate = request.DefaultRiskFreeRate;
        s.DefaultBenchmark = Asset.NormalizeSymbol(request.DefaultBenchmark);
        s.StalePriceDays = request.StalePriceDays;
        s.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    private SettingsDto ToDto(ApplicationSettings s) => new(
        s.ActiveProvider,
        s.DefaultCurrency,
        s.DefaultRiskFreeRate,
        s.DefaultBenchmark,
        s.StalePriceDays,
        providers.Providers.Select(MarketDataService.ToDto).ToList(),
        options.Value.ScheduledRefreshEnabled,
        options.Value.ScheduledRefreshIntervalHours);
}
