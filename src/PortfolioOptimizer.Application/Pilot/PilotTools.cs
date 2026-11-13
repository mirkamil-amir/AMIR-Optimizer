using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.MachineLearning.Training;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Pilot;

// Every tool reads the cycle's portfolio, mandate and decision time from the PilotToolContext (set from the stored pilot);
// none accepts a user or portfolio id, and every price series ends at the decision date.

// ====================================================================================================== READ

public sealed class PortfolioStateTool : PilotTool
{
    public override string Name => "get_portfolio_state";
    public override string Purpose => "Current holdings, weights, cash and value of the managed portfolio at the decision time, with data freshness.";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => "{}";

    public override Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var o = ctx.Observation;
        object result = new
        {
            asOf = o.AsOfUtc,
            currency = o.Currency,
            portfolioValue = o.PortfolioValue,
            cash = o.Cash,
            cashWeight = o.PortfolioValue is > 0 ? Math.Round((double)(o.Cash / o.PortfolioValue.Value), 4) : (double?)null,
            holdings = o.Holdings.Select(h =>
            {
                var info = ctx.Universe.GetValueOrDefault(h.Symbol);
                var holding = ctx.Portfolio.Holdings.FirstOrDefault(x => x.Symbol == h.Symbol);
                return new
                {
                    h.Symbol, h.Quantity, h.Value, h.Weight, h.PriceDate, info?.Sector, info?.Currency, info?.Country,
                    averageCost = holding?.AverageCost, inUniverse = info is not null,
                };
            }),
            freshness = o.Freshness,
            liveRecord = new { drawdownFromPeak = o.LiveDrawdown, returnSinceBaseline = o.LiveReturn },
            o.PricesSimulated,
            o.Notes,
        };
        return Task.FromResult(result);
    }

    public override string Summarize(object result) => "Portfolio state read.";
}

public sealed class MandateTool : PilotTool
{
    public override string Name => "get_mandate";
    public override string Purpose => "The owner's mandate (read-only): universe, position, holding, cash, turnover, sector/currency/country and risk limits.";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => "{}";

    public override Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct) => Task.FromResult<object>(new
    {
        version = ctx.Cycle.MandateVersion,
        mandate = ctx.Mandate,
        shortSellingAllowed = false,
        leverageAllowed = false,
        benchmark = ctx.Benchmark,
        note = "Read-only. Only the owner can change the mandate; requests for changes can be stated in mandateRequests of the decision.",
    });

    public override string Summarize(object result) => "Mandate read.";
}

public sealed class InvestableUniverseTool : PilotTool
{
    public override string Name => "get_investable_universe";
    public override string Purpose => "List the symbols the mandate allows, with sector, currency, country and whether a fresh price exists. Paged (150 per page).";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => """{"page"?: 1.., "sector"?: string, "currency"?: string}""";

    public override Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var page = PilotArgs.Int(args, "page", 1, 1, 20);
        var sector = args.TryGetProperty("sector", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var currency = args.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var items = ctx.Universe.Values
            .Where(a => sector is null || string.Equals(a.Sector, sector, StringComparison.OrdinalIgnoreCase))
            .Where(a => currency is null || string.Equals(a.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Symbol, StringComparer.Ordinal).ToList();
        object result = new
        {
            total = items.Count,
            page,
            pages = (items.Count + 149) / 150,
            priced = items.Count(a => a.Priced),
            sectors = ctx.Universe.Values.GroupBy(a => a.Sector ?? "Unclassified").ToDictionary(g => g.Key, g => g.Count()),
            items = items.Skip((page - 1) * 150).Take(150).Select(a => new { a.Symbol, a.Name, a.Sector, a.Currency, a.Country, a.Priced, a.PriceProblem }),
        };
        return Task.FromResult(result);
    }

    public override string Summarize(object result) => "Investable universe listed.";
}

public sealed class PastDecisionsTool(IApplicationDbContext db, PilotAnalytics analytics) : PilotTool
{
    public override string Name => "get_past_decisions";
    public override string Purpose => "This pilot's earlier decisions and how their target holdings moved since, measured only with prices up to the current decision date.";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => """{"limit"?: 1..10}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var limit = PilotArgs.Int(args, "limit", 5, 1, 10);
        var cycles = await db.MatrixPilotCycles.AsNoTracking()
            .Where(c => c.PilotId == ctx.PilotId && c.AsOfUtc < ctx.AsOfUtc && c.Outcome != PilotCycleOutcome.Pending && c.Id != ctx.Cycle.Id)
            .OrderByDescending(c => c.AsOfUtc).Take(limit).ToListAsync(ct);
        var decisions = cycles.Select(c => (Cycle: c, Decision: PilotJson.Deserialize<PilotDecision>(c.DecisionJson))).ToList();
        var symbols = decisions.SelectMany(d => d.Decision?.Targets.Select(t => Asset.NormalizeSymbol(t.Symbol)) ?? []).Distinct().ToList();
        var (series, _) = symbols.Count == 0 ? ([], new Dictionary<string, string>()) : await analytics.SeriesAsync(ctx, symbols, 400, ct);
        double? Since(string symbol, DateOnly from)
        {
            if (!series.TryGetValue(symbol, out var s)) return null;
            var start = s.Points.LastOrDefault(p => p.Date <= from);
            var end = s.Points.LastOrDefault();
            return start is null || end is null || start.Price <= 0 || end.Date <= start.Date ? null : Math.Round(end.Price / start.Price - 1, 4);
        }
        return new
        {
            note = $"Moves are measured from each decision date to {ctx.AsOfDate:yyyy-MM-dd} only; later prices are not used.",
            decisions = decisions.Select(d => new
            {
                date = d.Cycle.AsOfUtc,
                outcome = d.Cycle.Outcome.ToString(),
                reason = d.Cycle.OutcomeReason,
                action = d.Decision?.Action,
                approach = d.Decision?.Strategy.Approach,
                confidence = d.Decision?.Confidence,
                targets = d.Decision?.Targets.Select(t => new
                {
                    symbol = Asset.NormalizeSymbol(t.Symbol), t.Weight, moveSinceDecision = Since(Asset.NormalizeSymbol(t.Symbol), DateOnly.FromDateTime(d.Cycle.AsOfUtc)),
                }),
                keyRisks = d.Decision?.KeyRisks,
                invalidation = d.Decision?.InvalidationConditions,
            }),
        };
    }

    public override string Summarize(object result) => "Earlier decisions read.";
}

/// <summary>
/// Structured news signals only: counts of provider-tagged stories by event type and category. Headlines, summaries and
/// descriptions are third-party text and never reach the model (prompt-injection boundary, threat-model.md).
/// </summary>
public sealed class NewsSignalsTool(IApplicationDbContext db) : PilotTool
{
    public override string Name => "get_news_signals";
    public override string Purpose => "Counts of provider-tagged news by event type and category per symbol in a recent window (metadata only, no article text; not evidence of cause).";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => """{"symbols": [1..20 symbols], "days"?: 1..30}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var symbols = PilotArgs.Symbols(args, "symbols", 1, 20);
        var days = PilotArgs.Int(args, "days", 14, 1, 30);
        var from = ctx.AsOfUtc.AddDays(-days);
        var asOf = ctx.AsOfUtc;
        var rows = await db.NewsArticleAssets.AsNoTracking()
            .Where(x => symbols.Contains(x.Asset!.Symbol))
            .Join(db.NewsArticles.AsNoTracking(), x => x.ArticleId, a => a.Id, (x, a) => new { x.Asset!.Symbol, a.PublishedAtUtc, a.RetrievedAtUtc, a.EventType, a.Category, a.IsOfficialSource, a.IsSimulated })
            // Point in time: published AND retrieved before the decision (an article stored later was not available then).
            .Where(r => r.PublishedAtUtc >= from && r.PublishedAtUtc <= asOf && r.RetrievedAtUtc <= asOf)
            .ToListAsync(ct);
        return new
        {
            window = $"{from:yyyy-MM-dd} – {asOf:yyyy-MM-dd}",
            note = "Counts of articles the providers tagged with the symbol. Article text is deliberately not provided. News volume is not a forecast and does not show cause.",
            symbols = symbols.Select(s =>
            {
                var own = rows.Where(r => r.Symbol == s).ToList();
                return new
                {
                    symbol = s,
                    articles = own.Count,
                    latest = own.Count == 0 ? (DateTime?)null : own.Max(r => r.PublishedAtUtc),
                    byEventType = own.GroupBy(r => r.EventType?.ToString() ?? "Unclassified").ToDictionary(g => g.Key, g => g.Count()),
                    byCategory = own.GroupBy(r => r.Category.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    official = own.Count(r => r.IsOfficialSource),
                    simulated = own.Count(r => r.IsSimulated),
                };
            }),
        };
    }

    public override string Summarize(object result) => "News signals counted (metadata only).";
}

public sealed class ExistingOptimizationsTool(IApplicationDbContext db) : PilotTool
{
    public override string Name => "get_existing_optimizations";
    public override string Purpose => "Math Optimizations already calculated for this portfolio before the decision time (objective, estimates, largest weights).";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => """{"limit"?: 1..5}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var limit = PilotArgs.Int(args, "limit", 3, 1, 5);
        var runs = await db.OptimizationRuns.AsNoTracking().Include(r => r.Weights)
            .Where(r => r.PortfolioId == ctx.PortfolioId && r.OwnerId == ctx.OwnerId && r.Status == OptimizationRunStatus.Succeeded && r.CreatedAtUtc <= ctx.AsOfUtc)
            .OrderByDescending(r => r.CreatedAtUtc).Take(limit).ToListAsync(ct);
        return new
        {
            note = "Estimates are in-sample historical figures from each run's own window, not forecasts.",
            runs = runs.Select(r => new
            {
                r.CreatedAtUtc, objective = r.Objective.ToString(), r.ExpectedReturn, r.Volatility, r.SharpeRatio, r.PeriodStart, r.PeriodEnd,
                weights = r.Weights.Where(w => w.Weight >= 0.005).OrderByDescending(w => w.Weight).Take(25).Select(w => new { w.Symbol, weight = Math.Round(w.Weight, 4) }),
            }),
        };
    }

    public override string Summarize(object result) => "Existing Math Optimizations read.";
}

public sealed class MlSignalsTool(IApplicationDbContext db) : PilotTool
{
    public override string Name => "get_ml_signals";
    public override string Purpose => "Latest stock rankings of completed ML Optimizations linked to this portfolio, made before the decision time.";
    public override PilotToolCategory Category => PilotToolCategory.Read;
    public override string Arguments => """{"top"?: 5..60}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var top = PilotArgs.Int(args, "top", 30, 5, 60);
        var experiments = await db.MlExperiments.AsNoTracking()
            .Where(e => e.PortfolioId == ctx.PortfolioId && e.OwnerId == ctx.OwnerId && e.Status == JobStatus.Succeeded && e.CompletedAtUtc <= ctx.AsOfUtc)
            .OrderByDescending(e => e.CompletedAtUtc).Take(3).ToListAsync(ct);
        var ids = experiments.Select(e => e.Id).ToList();
        var sets = await db.MlPredictionSets.AsNoTracking()
            .Where(s => ids.Contains(s.ExperimentId) && s.CreatedAtUtc <= ctx.AsOfUtc && s.AsOfDate <= ctx.AsOfDate)
            .ToListAsync(ct);
        return new
        {
            note = "Rankings from models trained on past prices; check their out-of-sample record before relying on them. Not a forecast of returns.",
            experiments = experiments.Select(e =>
            {
                var set = sets.Where(s => s.ExperimentId == e.Id).OrderByDescending(s => s.AsOfDate).FirstOrDefault();
                var predictions = set is null ? [] : JsonSerializer.Deserialize<List<StockPrediction>>(set.PredictionsJson, MlJson.Options) ?? [];
                return new
                {
                    e.Name, e.ModelFamily, e.Target, e.HorizonDays, rankedAsOf = set?.AsOfDate,
                    predictions = predictions.OrderBy(p => p.Rank).Take(top).Select(p => new { p.Symbol, p.Rank, score = Math.Round(p.Score, 4), p.Selected, targetWeight = Math.Round(p.TargetWeight, 4) }),
                };
            }),
        };
    }

    public override string Summarize(object result) => "ML Optimization rankings read.";
}

// ====================================================================================================== ANALYZE

public sealed class ScreenUniverseTool(PilotAnalytics analytics, IApplicationDbContext db) : PilotTool
{
    public const int MaxScreened = 600;
    public override string Name => "screen_universe";
    public override string Purpose =>
        "Rank listings by a backward-looking metric (trailing return, volatility, return per volatility, drawdown) from stored prices. scope \"mandate\" (default) " +
        "= the mandate's investable universe; scope \"catalogue\" = every listing with recent prices, for your unconstrained portfolio (narrow it with indexes, " +
        "countries, sectors or currencies; at most 600 are screened).";
    public override PilotToolCategory Category => PilotToolCategory.Analyze;
    public override string Arguments => """{"sortBy": "momentum_12m"|"momentum_6m"|"momentum_3m"|"low_volatility"|"return_per_volatility"|"shallow_drawdown", "scope"?: "mandate"|"catalogue", "indexes"?: [..], "countries"?: [..], "sectors"?: [..], "currencies"?: [..], "limit"?: 5..50}""";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var sortBy = PilotArgs.Choice(args, "sortBy", null, "momentum_12m", "momentum_6m", "momentum_3m", "low_volatility", "return_per_volatility", "shallow_drawdown");
        var scope = PilotArgs.Choice(args, "scope", "mandate", "mandate", "catalogue");
        var indexes = PilotArgs.Strings(args, "indexes", 10);
        var countries = PilotArgs.Strings(args, "countries", 20);
        var sectors = PilotArgs.Strings(args, "sectors", 20);
        var currencies = PilotArgs.Strings(args, "currencies", 10);
        var limit = PilotArgs.Int(args, "limit", 25, 5, 50);
        if (scope == "catalogue" || indexes.Count > 0) await PilotCatalogue.EnsureLoadedAsync(ctx, db, ct);
        var source = scope == "catalogue" ? ctx.Catalogue!.Values : ctx.Universe.Values.Where(a => !ctx.Mandate.ExcludedSymbols.Contains(a.Symbol));
        var candidates = source.Where(a => a.Priced)
            .Where(a => indexes.Count == 0 || (ctx.Memberships!.GetValueOrDefault(a.Symbol) ?? []).Intersect(indexes, StringComparer.OrdinalIgnoreCase).Any())
            .Where(a => countries.Count == 0 || countries.Contains(a.Country ?? "", StringComparer.OrdinalIgnoreCase))
            .Where(a => sectors.Count == 0 || sectors.Contains(a.Sector ?? "", StringComparer.OrdinalIgnoreCase))
            .Where(a => currencies.Count == 0 || currencies.Contains(a.Currency, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Symbol).Order(StringComparer.Ordinal).ToList();
        if (candidates.Count > MaxScreened)
            throw new PilotToolException("invalid_arguments",
                $"{candidates.Count} listings match; at most {MaxScreened} can be screened at once. Narrow it with indexes, countries, sectors or currencies.");
        var pool = candidates;
        var metrics = await analytics.MetricsAsync(ctx, pool, ct);
        Func<PilotAssetMetrics, double?> key = sortBy switch
        {
            "momentum_12m" => m => m.Return12M,
            "momentum_6m" => m => m.Return6M,
            "momentum_3m" => m => m.Return3M,
            "low_volatility" => m => -m.Volatility12M,
            "return_per_volatility" => m => m.ReturnPerVolatility12M,
            _ => m => m.MaxDrawdown12M is { } d ? -Math.Abs(d) : null,
        };
        var ranked = metrics.Values.Where(m => key(m) is not null).OrderByDescending(m => key(m)).ThenBy(m => m.Symbol, StringComparer.Ordinal).ToList();
        return new
        {
            sortBy,
            scope,
            screened = pool.Count,
            withMetric = ranked.Count,
            withoutEnoughHistory = pool.Count - ranked.Count,
            note = "Backward-looking: past returns and volatility do not predict future returns. Values in the portfolio currency.",
            top = ranked.Take(limit).Select(Row),
        };
    }

    internal static object Row(PilotAssetMetrics m) => new
    {
        m.Symbol, m.Sector, m.Currency, m.Country, m.LastPriceDate,
        r1m = R(m.Return1M), r3m = R(m.Return3M), r6m = R(m.Return6M), r12m = R(m.Return12M),
        vol12m = R(m.Volatility12M), maxDd12m = R(m.MaxDrawdown12M), retPerVol = R(m.ReturnPerVolatility12M),
    };

    private static double? R(double? v) => v is { } x ? Math.Round(x, 4) : null;

    public override string Summarize(object result) => "Universe screened.";
}

public sealed class SecurityMetricsTool(PilotAnalytics analytics, MarketData.PriceQueryService prices, IApplicationDbContext db) : PilotTool
{
    public override string Name => "get_security_metrics";
    public override string Purpose =>
        "Trailing returns, volatility, drawdown, beta to the benchmark and average traded volume for specific symbols (any listing of the catalogue; " +
        "inMandateUniverse says whether the mandate portfolio may hold it).";
    public override PilotToolCategory Category => PilotToolCategory.Analyze;
    public override string Arguments => """{"symbols": [1..30 symbols]}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var symbols = PilotArgs.Symbols(args, "symbols", 1, 30);
        if (symbols.Any(s => !ctx.Universe.ContainsKey(s))) await PilotCatalogue.EnsureLoadedAsync(ctx, db, ct);
        var outside = symbols.Where(s => ctx.Info(s) is null).ToList();
        var inside = symbols.Except(outside).ToList();
        var metrics = await analytics.MetricsAsync(ctx, inside, ct);
        var betas = new Dictionary<string, double?>();
        foreach (var s in inside)
        {
            var risk = await analytics.AnalyzeAsync(ctx, new Dictionary<string, double> { [s] = 1 }, 252, ct);
            betas[s] = risk.BetaToBenchmark is { } b ? Math.Round(b, 3) : null;
        }
        var volumes = await prices.GetDailyObservationsAsync(inside, ctx.PriceSource, ctx.AsOfDate.AddDays(-40), ctx.AsOfDate, ct);
        return new
        {
            benchmark = ctx.Benchmark,
            unknownOrWithoutRecentPrices = outside,
            securities = inside.Where(metrics.ContainsKey).Select(s => new
            {
                metrics = ScreenUniverseTool.Row(metrics[s]),
                inMandateUniverse = ctx.Universe.ContainsKey(s),
                betaToBenchmark12m = betas.GetValueOrDefault(s),
                averageDailyVolume20d = volumes.TryGetValue(s, out var v) && v.Bars.Any(b => b.Volume is not null)
                    ? (long?)v.Bars.TakeLast(20).Where(b => b.Volume is not null).Average(b => (double)b.Volume!.Value) : null,
            }),
        };
    }

    public override string Summarize(object result) => "Security metrics computed.";
}

public sealed class CorrelationTool(PilotAnalytics analytics) : PilotTool
{
    public override string Name => "get_correlations";
    public override string Purpose => "Correlation matrix of daily returns (portfolio currency) for 2–25 symbols over a trailing window.";
    public override PilotToolCategory Category => PilotToolCategory.Analyze;
    public override string Arguments => """{"symbols": [2..25 symbols], "lookbackDays"?: 126..756}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var symbols = PilotArgs.Symbols(args, "symbols", 2, 25);
        var lookback = PilotArgs.Int(args, "lookbackDays", 252, 126, 756);
        var (series, excluded) = await analytics.SeriesAsync(ctx, symbols, lookback + 1, ct);
        var usable = symbols.Where(s => series.TryGetValue(s, out var p) && p.Points.Count >= 61 && !excluded.ContainsKey(s)).ToList();
        if (usable.Count < 2) throw new PilotToolException("failed", "Fewer than two symbols have enough stored prices.");
        var matrix = ReturnMatrix.Build(usable.ToDictionary(s => s, s => series[s].Points, StringComparer.OrdinalIgnoreCase), usable);
        if (matrix.Observations < 30) throw new PilotToolException("failed", $"Only {matrix.Observations} aligned daily returns.");
        var est = CovarianceEstimator.Estimate(matrix.Returns, CovarianceMethod.Sample, AnalyticsConventions.Default);
        var corr = CovarianceEstimator.ToCorrelation(est.Covariance);
        return new
        {
            window = $"{matrix.PriceDates[0]:yyyy-MM-dd} – {matrix.PriceDates[^1]:yyyy-MM-dd}",
            observations = matrix.Observations,
            symbols = usable,
            excluded = symbols.Except(usable),
            matrix = Enumerable.Range(0, usable.Count).Select(i => Enumerable.Range(0, usable.Count).Select(j => Math.Round(corr[i, j], 2)).ToArray()),
        };
    }

    public override string Summarize(object result) => "Correlations computed.";
}

public sealed class AnalyzePortfolioTool(PilotAnalytics analytics, IApplicationDbContext db) : PilotTool
{
    public override string Name => "analyze_portfolio";
    public override string Purpose => "Risk of a candidate allocation: ex-ante volatility, risk shares, diversification, VaR/ES, drawdown, worst periods, beta, sector/currency/country exposure.";
    public override PilotToolCategory Category => PilotToolCategory.Analyze;
    public override string Arguments => """{"weights": {"SYMBOL": fraction, ...} (1..60, sum ≤ 1; rest is cash), "lookbackDays"?: 126..756}""";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var weights = PilotArgs.Weights(args, "weights", 60);
        var lookback = PilotArgs.Int(args, "lookbackDays", 504, 126, 756);
        if (weights.Keys.Any(s => !ctx.Universe.ContainsKey(s))) await PilotCatalogue.EnsureLoadedAsync(ctx, db, ct);
        return await analytics.AnalyzeAsync(ctx, weights, lookback, ct);
    }

    public override string Summarize(object result) => result is PilotRiskAnalysis r && r.Summary.Volatility is { } v
        ? $"Ex-ante volatility {v:P1}, effective holdings {r.Summary.EffectiveHoldings:0.0} ({r.Summary.Window})." : "Risk could not be computed.";
}

// ====================================================================================================== SIMULATE

public sealed class OptimizerTool(OptimizationService optimizations, MarketData.PriceQueryService prices) : PilotTool
{
    /// <summary>The optimizer always computes a frontier (minimum 2 points); the tool does not use it, so the minimum keeps it fast.</summary>
    private const int FrontierPoints = 2;
    public override string Name => "run_optimizer";
    public override string Purpose => "Run the platform's long-only mean-variance optimizer on chosen symbols with the mandate's caps (nothing is saved).";
    public override PilotToolCategory Category => PilotToolCategory.Simulate;
    public override string Arguments => """{"symbols": [2..40], "objective": "MinimumVariance"|"MaximumSharpe"|"EqualWeight", "maxWeight"?: fraction ≤ mandate max, "covariance"?: "LedoitWolf"|"Sample", "lookbackDays"?: 252..1260}""";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var symbols = PilotArgs.Symbols(args, "symbols", 2, 40);
        var outside = symbols.Where(s => !ctx.Universe.ContainsKey(s)).ToList();
        if (outside.Count > 0) throw new PilotToolException("invalid_arguments", $"Outside the mandate's universe: {string.Join(", ", outside)}.");
        var objective = PilotArgs.Choice(args, "objective", null, "MinimumVariance", "MaximumSharpe", "EqualWeight");
        var maxWeight = PilotArgs.Number(args, "maxWeight", ctx.Mandate.MaxPositionWeight, 0.01, ctx.Mandate.MaxPositionWeight);
        var covariance = PilotArgs.Choice(args, "covariance", "LedoitWolf", "LedoitWolf", "Sample");
        var lookback = PilotArgs.Int(args, "lookbackDays", 504, 252, 1260);
        var invested = 1 - ctx.Mandate.MinCashWeight;
        if (symbols.Count * maxWeight < invested - 1e-9)
            throw new PilotToolException("invalid_arguments", $"{symbols.Count} symbols at most {maxWeight:P0} each cannot reach {invested:P0} invested.");

        var app = await prices.SettingsAsync(ct);
        var settings = new PortfolioSettings
        {
            Universe = symbols,
            ExcludedSymbols = [.. ctx.Mandate.ExcludedSymbols],
            Objective = Enum.Parse<ObjectiveKind>(objective),
            CovarianceMethod = Enum.Parse<CovarianceKind>(covariance),
            LookbackDays = lookback,
            MaxWeight = maxWeight,
            MinWeight = 0,
            MaxHoldings = ctx.Mandate.MaxHoldings,
            MinPositionWeight = ctx.Mandate.MinPositionWeight > 0 ? ctx.Mandate.MinPositionWeight : null,
            CashReserve = ctx.Mandate.MinCashWeight,
            SectorLimits = ctx.Mandate.SectorLimits.ToDictionary(),
            RiskFreeRate = app.DefaultRiskFreeRate,
            BenchmarkSymbol = ctx.Benchmark,
            TransactionCostBps = ctx.Portfolio.Settings.TransactionCostBps,
        };
        var (run, _) = await optimizations.EvaluateAsync(ctx.Portfolio, ctx.OwnerId, settings, FrontierPoints, ct, asOf: ctx.AsOfDate);
        return new
        {
            status = run.Status.ToString(),
            message = run.Message,
            window = run.PeriodStart is null ? null : $"{run.PeriodStart:yyyy-MM-dd} – {run.PeriodEnd:yyyy-MM-dd}",
            expectedReturn = run.ExpectedReturn, volatility = run.Volatility, sharpe = run.SharpeRatio,
            cashWeight = ctx.Mandate.MinCashWeight,
            weights = run.Weights.Where(w => w.Weight >= 0.001).OrderByDescending(w => w.Weight).Select(w => new { w.Symbol, weight = Math.Round(w.Weight, 4) }),
            diagnostics = run.Diagnostics.Take(8),
            note = "Estimates are historical, in-sample and noisy – not forecasts. The optimizer's weights are a suggestion; your decision is validated separately.",
        };
    }

    public override string Summarize(object result) => "Optimizer run (not saved).";
}

public sealed class EstimateTradesTool(PilotAnalytics analytics) : PilotTool
{
    public override string Name => "estimate_trades";
    public override string Purpose => "Orders, turnover and transaction costs that target weights would cause now (same calculator as a real proposal; nothing is booked).";
    public override PilotToolCategory Category => PilotToolCategory.Simulate;
    public override string Arguments => """{"weights": {"SYMBOL": fraction, ...} (1..60, sum ≤ 1; rest is cash)}""";

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var weights = PilotArgs.Weights(args, "weights", 60);
        var outside = weights.Keys.Where(s => !ctx.Universe.ContainsKey(s)).ToList();
        if (outside.Count > 0) throw new PilotToolException("invalid_arguments", $"Outside the mandate's universe: {string.Join(", ", outside)}.");
        return await analytics.EstimateTradesAsync(ctx, weights, ct);
    }

    public override string Summarize(object result) => result is PilotTradeEstimate t
        ? $"{t.Buys} buy(s), {t.Sells} sell(s), turnover {t.Turnover:P1}, costs {t.Costs:N2}." : "Trades estimated.";
}

/// <summary>
/// The mandate check, available during research: the same validator that decides whether a proposal may be booked, run on a
/// candidate. Returns every breach with the proposed and the allowed value, so the model can fix its portfolio before deciding.
/// </summary>
public sealed class CheckAllocationTool(PilotFitChecker fit) : PilotTool
{
    public override string Name => "check_allocation";
    public override string Purpose =>
        "Check a candidate mandate portfolio against every mandate rule with the platform's validator (positions, holdings, cash, sectors, countries, " +
        "currencies, turnover, volatility, minimum trade). Returns passed or each breach with proposed and allowed values. Use it until your candidate passes.";
    public override PilotToolCategory Category => PilotToolCategory.Simulate;
    public override string Arguments => """{"weights": {"SYMBOL": fraction, ...} (1..60), "cashWeight"?: fraction (default 1 − sum)}""";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override async Task<object> RunAsync(PilotToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var weights = PilotArgs.Weights(args, "weights", 60);
        var cash = PilotArgs.Number(args, "cashWeight", Math.Round(1 - weights.Values.Sum(), 6), 0, 1);
        var candidate = new PilotDecision
        {
            Action = "REBALANCE", CashWeight = cash,
            Targets = weights.Select(kv => new PilotTarget { Symbol = kv.Key, Weight = kv.Value }).ToList(),
        };
        var r = await fit.CheckAsync(ctx, candidate, requireTexts: false, ct);
        return new
        {
            passed = r.Fits,
            violations = r.Validation.Violations.Select(v => new { v.Rule, v.Detail, v.Symbol, v.Proposed, v.Allowed }),
            checks = r.Validation.Checks.Select(c => new { c.Name, c.Passed, c.Detail }),
            volatility = r.Risk?.Summary.Volatility,
            turnover = r.Validation.Trades?.Turnover,
            note = r.Fits ? "This candidate fits every mandate rule." : "Fix every violation before deciding; the final decision is checked the same way.",
        };
    }

    public override string Summarize(object result) =>
        JsonSerializer.SerializeToElement(result, PilotJson.Options) is var e && e.GetProperty("passed").GetBoolean()
            ? "Candidate fits the mandate." : $"Candidate breaks {e.GetProperty("violations").GetArrayLength()} rule(s).";
}
