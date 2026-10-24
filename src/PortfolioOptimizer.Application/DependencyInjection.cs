using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Administration;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Backtesting;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Application.Settings;
using PortfolioOptimizer.Application.Comparisons;
using PortfolioOptimizer.Application.Trading;
using PortfolioOptimizer.Application.Watchlists;
using PortfolioOptimizer.Application.News;
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
        services.AddScoped<Fx.FxService>();
        services.AddSingleton<Fx.FxFetchThrottle>();
        services.AddSingleton<Fx.FxDownloader>();
        services.AddScoped<MarketDataService>();
        services.AddScoped<DataReadinessService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<PortfolioService>();
        services.AddScoped<PortfolioTransactionService>();
        services.AddScoped<PortfolioValuationService>();
        services.AddScoped<OptimizationService>();
        services.AddScoped<TradingService>();
        services.AddScoped<PortfolioAnalyticsService>();
        services.AddScoped<RealityCheckService>();
        services.AddScoped<BacktestService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<CatalogueService>();
        services.AddScoped<StockDiscoveryService>();
        services.AddScoped<CompanyProfileService>();
        services.AddScoped<Research.ToolRunner>();
        services.AddScoped<Research.BriefInterpreter>();
        services.AddScoped<Research.RefinementInterpreter>();
        services.AddScoped<Research.InterpretationService>();
        services.AddScoped<Research.SearchUniverseTool>();
        services.AddScoped<Research.EvaluateHypothesisTool>();
        services.AddScoped<Research.ReviewHypothesisTool>();
        services.AddScoped<Research.CompanyContextTool>();
        services.AddScoped<Research.SaveHypothesisTool>();
        services.AddScoped<Research.ResearchAgent>();
        services.AddScoped<QuoteRefreshService>();
        services.AddScoped<UniverseService>();
        services.AddScoped<MlDataLoader>();
        services.AddScoped<MlExperimentService>();
        services.AddScoped<MlExperimentRunner>();
        services.AddSingleton<StrategyComparisonEngine>();
        services.AddScoped<StrategyComparisonService>();
        services.AddScoped<StrategyComparisonRunner>();
        services.AddScoped<WatchlistService>();
        services.AddScoped<NewsService>();
        return services;
    }
}
