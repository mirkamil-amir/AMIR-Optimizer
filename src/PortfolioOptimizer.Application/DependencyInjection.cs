using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Backtesting;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Settings;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Solvers;

namespace PortfolioOptimizer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(AnalyticsConventions.Default);
        services.AddSingleton<IQuadraticProgramSolver, AdmmQpSolver>();
        services.AddSingleton<IPortfolioOptimizer, MeanVarianceOptimizer>();
        services.AddSingleton<BacktestEngine>();
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<PriceQueryService>();
        services.AddScoped<MarketDataService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<PortfolioService>();
        services.AddScoped<OptimizationService>();
        services.AddScoped<TradingService>();
        services.AddScoped<PortfolioAnalyticsService>();
        services.AddScoped<BacktestService>();
        return services;
    }
}
