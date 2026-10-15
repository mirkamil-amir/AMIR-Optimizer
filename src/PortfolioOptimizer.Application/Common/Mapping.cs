using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization.Backtesting;
using PortfolioOptimizer.Optimization.Portfolio;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Common;

public static class Mapping
{
    public static PortfolioSettings ToDomain(this PortfolioSettingsDto d) => new()
    {
        Universe = d.Universe.Select(Asset.NormalizeSymbol).Distinct().ToList(),
        LookbackDays = d.LookbackDays,
        Objective = d.Objective,
        CovarianceMethod = d.CovarianceMethod,
        RiskFreeRate = d.RiskFreeRate,
        BenchmarkSymbol = Asset.NormalizeSymbol(d.BenchmarkSymbol),
        MinWeight = d.MinWeight,
        MaxWeight = d.MaxWeight,
        MaxHoldings = d.MaxHoldings,
        MinPositionWeight = d.MinPositionWeight,
        CashReserve = d.CashReserve,
        MaxTurnover = d.MaxTurnover,
        SectorLimits = new Dictionary<string, double>(d.SectorLimits, StringComparer.OrdinalIgnoreCase),
        ExcludedSymbols = d.ExcludedSymbols.Select(Asset.NormalizeSymbol).Distinct().ToList(),
        TargetReturn = d.TargetReturn,
        MaxVolatility = d.MaxVolatility,
        TransactionCostBps = d.TransactionCostBps,
        MinimumFee = d.MinimumFee,
        AllowFractionalShares = d.AllowFractionalShares,
        RebalanceFrequency = d.RebalanceFrequency,
        RebalanceDriftThreshold = d.RebalanceDriftThreshold,
    };

    public static PortfolioSettingsDto ToDto(this PortfolioSettings s) => new()
    {
        Universe = [.. s.Universe],
        LookbackDays = s.LookbackDays,
        Objective = s.Objective,
        CovarianceMethod = s.CovarianceMethod,
        RiskFreeRate = s.RiskFreeRate,
        BenchmarkSymbol = s.BenchmarkSymbol,
        MinWeight = s.MinWeight,
        MaxWeight = s.MaxWeight,
        MaxHoldings = s.MaxHoldings,
        MinPositionWeight = s.MinPositionWeight,
        CashReserve = s.CashReserve,
        MaxTurnover = s.MaxTurnover,
        SectorLimits = new Dictionary<string, double>(s.SectorLimits),
        ExcludedSymbols = [.. s.ExcludedSymbols],
        TargetReturn = s.TargetReturn,
        MaxVolatility = s.MaxVolatility,
        TransactionCostBps = s.TransactionCostBps,
        MinimumFee = s.MinimumFee,
        AllowFractionalShares = s.AllowFractionalShares,
        RebalanceFrequency = s.RebalanceFrequency,
        RebalanceDriftThreshold = s.RebalanceDriftThreshold,
    };

    public static OptimizationObjective ToOptimization(this ObjectiveKind k) => k switch
    {
        ObjectiveKind.MinimumVariance => OptimizationObjective.MinimumVariance,
        ObjectiveKind.MaximumSharpe => OptimizationObjective.MaximumSharpe,
        ObjectiveKind.TargetReturn => OptimizationObjective.TargetReturn,
        ObjectiveKind.MaximumReturnForRisk => OptimizationObjective.MaximumReturnForRisk,
        ObjectiveKind.EqualWeight => OptimizationObjective.EqualWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static CovarianceMethod ToOptimization(this CovarianceKind k) =>
        k == CovarianceKind.Sample ? CovarianceMethod.Sample : CovarianceMethod.LedoitWolf;

    public static RebalanceFrequency ToOptimization(this RebalanceKind k) => k switch
    {
        RebalanceKind.Monthly => RebalanceFrequency.Monthly,
        RebalanceKind.Quarterly => RebalanceFrequency.Quarterly,
        _ => RebalanceFrequency.None,
    };

    public static PortfolioConstraints ToConstraints(this PortfolioSettings s, IReadOnlyDictionary<string, string> sectors) => new()
    {
        MinWeight = s.MinWeight,
        MaxWeight = s.MaxWeight,
        MaxHoldings = s.MaxHoldings,
        MinPositionWeight = s.MinPositionWeight is > 0 ? s.MinPositionWeight : null,
        CashReserve = s.CashReserve,
        MaxTurnover = s.MaxTurnover,
        Sectors = sectors,
        SectorMaxWeights = new Dictionary<string, double>(s.SectorLimits, StringComparer.OrdinalIgnoreCase),
        ExcludedSymbols = s.ExcludedSymbols,
        TargetReturn = s.TargetReturn,
        MaxVolatility = s.MaxVolatility,
    };

    public static AssetDto ToDto(this Asset a) =>
        new(a.Id, a.Symbol, a.Name, a.Exchange, a.Currency, a.AssetType, a.Sector, a.IsEnabled);

    public static TransactionDto ToDto(this TradeExecution e) =>
        new(e.Id, e.OrderId, e.Symbol, e.Side, e.Quantity, e.Price, e.PriceDate, e.GrossAmount, e.Fees, e.NetCashFlow, e.RealizedPnl, e.ExecutedAtUtc,
            e.Currency, e.FxRate, e.FxRateDate, e.FxSource);

    public static OrderDto ToDto(this SimulatedOrder o) =>
        new(o.Id, o.BatchId, o.ProposalId, o.Symbol, o.Side, o.RequestedQuantity, o.FilledQuantity, o.Status, o.ExecutionPrice, o.CreatedAtUtc, o.Message,
            o.Currency, o.FxRate);

    public static SyncJobDto ToDto(this MarketDataSyncJob j) => new(
        j.Id, j.Status, j.Source,
        j.Symbols.Split(',', StringSplitOptions.RemoveEmptyEntries),
        j.StartDate, j.EndDate, j.RequestedAtUtc, j.StartedAtUtc, j.CompletedAtUtc,
        j.AssetsTotal, j.AssetsCompleted, j.AssetsFailed, j.Message,
        j.Logs.OrderBy(l => l.Symbol).Select(l => new SyncLogDto(l.Symbol, l.Success, l.RowsReceived, l.RowsInserted, l.RowsUpdated, l.RowsRejected, l.DuplicatesSkipped, l.Message, l.CompletedAtUtc)).ToList());
}
