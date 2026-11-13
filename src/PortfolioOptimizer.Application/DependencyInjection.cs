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
        services.AddScoped<PortfolioStrategyService>();
        services.AddScoped<StrategyComparisonRunner>();
        services.AddScoped<WatchlistService>();
        services.AddScoped<NewsService>();
        services.AddScoped<WireFeedService>();
        // Matrix Pilot (docs/matrix-pilot.md): the model may use only the tools registered here (READ, ANALYZE, SIMULATE).
        services.AddScoped<Common.BackgroundActor>();
        services.AddScoped<Pilot.PilotJournal>();
        services.AddScoped<Pilot.PilotAnalytics>();
        services.AddScoped<Pilot.PilotToolRunner>();
        services.AddScoped<Pilot.PilotTool, Pilot.PortfolioStateTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.MandateTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.InvestableUniverseTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.PastDecisionsTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.NewsSignalsTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.ExistingOptimizationsTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.MlSignalsTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.ScreenUniverseTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.SecurityMetricsTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.CorrelationTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.AnalyzePortfolioTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.OptimizerTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.EstimateTradesTool>();
        services.AddScoped<Pilot.PilotTool, Pilot.CheckAllocationTool>();
        services.AddScoped<Pilot.PilotFitChecker>();
        services.AddScoped<Pilot.PilotEngine>();
        services.AddScoped<Pilot.PilotService>();
        return services;
    }
}
