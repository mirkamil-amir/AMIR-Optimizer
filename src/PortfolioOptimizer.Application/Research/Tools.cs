using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Analytics;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Application.Optimization;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Optimization;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.Application.Research;

// ====================================================================== search_investable_universe

/// <summary>
/// Shortlists candidates from one index with a transparent, reproducible rule: drop excluded sectors/symbols and
/// stocks without enough stored history or with a stale price, then fill the shortlist round-robin across sectors,
/// ranking within each sector by the disclosed criterion. A research filter – not a judgement of quality.
/// </summary>
public sealed class SearchUniverseTool(StockDiscoveryService stocks, PriceQueryService prices, IApplicationDbContext db, TimeProvider clock)
    : ResearchTool<ResearchBrief, CandidateSetDto>
{
    public override string Name => "search_investable_universe";
    public override string Purpose => "Shortlist index members that meet the brief's criteria and data requirements, with the reason for each inclusion or exclusion.";
    public override ToolCategory Category => ToolCategory.ReadOnlyResearch;

    public override void Validate(ResearchBrief args)
    {
        if (!BriefRules.IsMarket(args.Market) || args.Index is null) throw new ToolException("invalid_arguments", "The brief has no market yet.");
        if (args.CandidateCount is < 4 or > 25) throw new ToolException("invalid_arguments", "Candidate count must be between 4 and 25.");
    }

    public override async Task<CandidateSetDto> ExecuteAsync(ToolContext context, ResearchBrief brief, CancellationToken ct)
    {
        var settings = await prices.SettingsAsync(ct);
        var members = new List<StockListItemDto>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await stocks.SearchAsync(new StockQuery(Index: brief.Index, PageSize: 200, Page: page), ct);
            members.AddRange(result.Items);
            if (members.Count >= result.TotalCount) break;
        }
        if (members.Count == 0) throw new ToolException("failed", $"The index {brief.Index} has no members in the catalogue.");

        var required = brief.LookbackDays + 1;
        var excludedSymbols = brief.ExcludeSymbols.Select(Asset.NormalizeSymbol).ToHashSet();
        var candidates = new List<CandidateDto>();
        var eligible = new List<StockListItemDto>();
        foreach (var m in members)
        {
            string? reason =
                excludedSymbols.Contains(m.Symbol) ? "Excluded by you." :
                m.Sector is { } s && brief.ExcludeSectors.Contains(s) ? $"Sector {s} excluded by you." :
                m.Observations < required ? $"Only {m.Observations} stored daily prices; {required} are needed for the {brief.LookbackDays}-day window." :
                m.Quote.IsStale ? $"Latest price ({m.Quote.PriceDate:yyyy-MM-dd}) is stale." : null;
            if (reason is null) eligible.Add(m);
            else candidates.Add(Candidate(m, false, reason, null));
        }

        // One-year behaviour of every eligible stock from stored adjusted closes.
        var metrics = new Dictionary<string, CandidateMetrics>();
        var symbols = eligible.Select(e => e.Symbol).ToList();
        var series = await prices.GetSeriesAsync(symbols, settings.ActiveProvider, null, null, ct, 253);
        foreach (var (symbol, s) in series)
            if (CandidateMetrics.FromPrices(s.Points.Select(p => p.Price).ToList()) is { } cm) metrics[symbol] = cm;

        var high = brief.RiskPreference == "high";
        var criterion = high
            ? "Sector-balanced; within each sector the highest one-year return per unit of volatility first (backward-looking – recent winners do not stay winners)."
            : "Sector-balanced; within each sector the lowest one-year volatility first (backward-looking – past volatility can change).";
        double Rank(StockListItemDto e) => metrics.TryGetValue(e.Symbol, out var m)
            ? high ? -(m.Volatility > 0 ? m.Return / m.Volatility : 0) : m.Volatility
            : double.MaxValue;
        var bySector = eligible.Where(e => metrics.ContainsKey(e.Symbol))
            .GroupBy(e => e.Sector ?? "Unclassified")
            .Select(g => new Queue<StockListItemDto>(g.OrderBy(Rank).ThenBy(e => e.Symbol)))
            .OrderByDescending(q => q.Count).ThenBy(q => q.Peek().Sector)
            .ToList();
        var chosen = new List<StockListItemDto>();
        while (chosen.Count < brief.CandidateCount && bySector.Any(q => q.Count > 0))
            foreach (var q in bySector.Where(q => q.Count > 0))
            {
                if (chosen.Count >= brief.CandidateCount) break;
                chosen.Add(q.Dequeue());
            }
        var chosenSet = chosen.Select(c => c.Symbol).ToHashSet();
        foreach (var e in eligible)
        {
            metrics.TryGetValue(e.Symbol, out var m);
            if (chosenSet.Contains(e.Symbol))
                candidates.Add(Candidate(e, true, $"Meets the data requirements; selected for sector {e.Sector ?? "Unclassified"} by the stated criterion.", m));
            else if (m is null)
                candidates.Add(Candidate(e, false, "One-year price behaviour could not be computed from the stored prices.", null));
            else
                candidates.Add(Candidate(e, false, "Meets the data requirements but ranked below the shortlist size in its sector.", m));
        }

        var warnings = new List<string>();
        if (chosen.Count < 3) warnings.Add($"Only {chosen.Count} stock(s) meet the data requirements. Download price history for more {brief.Index} members or shorten the history window.");
        if (candidates.Any(c => c.IsSimulated)) warnings.Add("Prices are SIMULATED (mock provider): no conclusion applies to real markets.");
        var stale = candidates.Count(c => c.StalePrice);
        if (stale > 0) warnings.Add($"{stale} member(s) have stale prices and were excluded.");
        var missing = candidates.Count(c => !c.Included && c.Reason.StartsWith("Only "));
        if (missing > 0) warnings.Add($"{missing} member(s) lack enough stored history; the shortlist is drawn from the rest only.");
        _ = db;

        var ordered = candidates.OrderByDescending(c => c.Included).ThenBy(c => c.Sector).ThenBy(c => c.Symbol).ToList();
        return new CandidateSetDto(brief.Market!, brief.Index, criterion, clock.GetUtcNow().UtcDateTime, settings.ActiveProvider, ordered,
            chosen.Select(c => c.Symbol).ToList(), warnings);
    }

    private static CandidateDto Candidate(StockListItemDto s, bool included, string reason, CandidateMetrics? m) =>
        new(s.Symbol, s.Name, s.Sector, s.Currency, included, reason, s.Observations, s.LastDate, s.Quote.IsStale,
            m?.Volatility, m?.Return, m?.MaxDrawdown, s.Quote.IsSimulated);

    public override string Summarize(ResearchBrief args, CandidateSetDto result) =>
        $"{result.Candidates.Count} {args.Index} members screened, {result.SelectedSymbols.Count} shortlisted ({result.Candidates.Count(c => !c.Included)} excluded with reasons).";
}

/// <summary>One-year behaviour of a price series (pure).</summary>
public sealed record CandidateMetrics(double Volatility, double Return, double MaxDrawdown)
{
    public static CandidateMetrics? FromPrices(IReadOnlyList<double> prices)
    {
        if (prices.Count < 60 || prices.Any(p => p <= 0)) return null;
        var returns = ReturnCalculator.SimpleReturns(prices);
        var vol = new PerformanceMetrics(AnalyticsConventions.Default).AnnualizedVolatility(returns).Value ?? 0;
        var dd = PerformanceMetrics.MaxDrawdown(prices).Value ?? 0;
        return new CandidateMetrics(vol, prices[^1] / prices[0] - 1, dd);
    }
}

// ====================================================================== run_portfolio_optimization

/// <summary>Runs the mean-variance engine for a hypothesis configuration. Returns the unsaved run record (nothing persisted).</summary>
public sealed class EvaluateHypothesisTool(OptimizationService optimizations, PriceQueryService prices, IApplicationDbContext db)
    : ResearchTool<HypothesisConfig, OptimizationRun>
{
    public override string Name => "run_portfolio_optimization";
    public override string Purpose => "Calculate portfolio weights with the implemented optimizer for an explicit configuration.";
    public override ToolCategory Category => ToolCategory.Calculation;

    public override void Validate(HypothesisConfig c)
    {
        if (!BriefRules.Objectives.Contains(c.Objective)) throw new ToolException("invalid_arguments", $"Unsupported objective {c.Objective}.");
        if (c.MaxWeight is < 0.05 or > 1) throw new ToolException("invalid_arguments", "Max weight must be between 5 % and 100 %.");
        if (c.Universe.Count is < 2 or > 40) throw new ToolException("invalid_arguments", "The universe needs 2–40 stocks.");
        if (c.Universe.Except(c.ExcludedSymbols).Count() * c.MaxWeight < 1 - 1e-9)
            throw new ToolException("invalid_arguments", $"Infeasible: {c.Universe.Except(c.ExcludedSymbols).Count()} stocks at most {c.MaxWeight:P0} each cannot add up to 100 %.");
        if (c.CovarianceMethod is not ("Sample" or "LedoitWolf")) throw new ToolException("invalid_arguments", "Covariance must be Sample or LedoitWolf.");
        if (c.LookbackDays is < 126 or > 1260) throw new ToolException("invalid_arguments", "History window must be 126–1260 days.");
        if (c.SectorLimits.Values.Any(v => v is < 0.05 or > 1)) throw new ToolException("invalid_arguments", "Sector limits must be between 5 % and 100 %.");
    }

    public override async Task<OptimizationRun> ExecuteAsync(ToolContext context, HypothesisConfig c, CancellationToken ct)
    {
        var app = await prices.SettingsAsync(ct);
        var universe = c.Universe.Select(Asset.NormalizeSymbol).ToList();
        var known = await db.Assets.AsNoTracking().Where(a => universe.Contains(a.Symbol)).Select(a => a.Symbol).ToListAsync(ct);
        var unknown = universe.Except(known).ToList();
        if (unknown.Count > 0) throw new ToolException("invalid_arguments", $"Unknown symbols: {string.Join(", ", unknown)}.");
        var settings = new PortfolioSettings
        {
            Universe = universe,
            ExcludedSymbols = c.ExcludedSymbols.ToList(),
            Objective = Enum.Parse<ObjectiveKind>(c.Objective),
            CovarianceMethod = Enum.Parse<CovarianceKind>(c.CovarianceMethod),
            LookbackDays = c.LookbackDays,
            MaxWeight = c.MaxWeight,
            MinWeight = 0,
            CashReserve = 0,
            SectorLimits = c.SectorLimits.ToDictionary(),
            RiskFreeRate = app.DefaultRiskFreeRate,
            BenchmarkSymbol = app.DefaultBenchmark,
            TransactionCostBps = c.TransactionCostBps,
        };
        var (run, _) = await optimizations.EvaluateAsync(null, context.UserId, settings, 20, ct);
        return run;
    }

    public override string Summarize(HypothesisConfig args, OptimizationRun run) => run.Status == OptimizationRunStatus.Succeeded
        ? $"{args.Objective} on {run.Symbols.Count} stocks: {run.Weights.Count(w => w.Weight >= 0.005)} positions, volatility {run.Volatility:P1} (estimate), window {run.PeriodStart:yyyy-MM-dd}–{run.PeriodEnd:yyyy-MM-dd}."
        : $"{args.Objective}: {run.Status} – {run.Message}";
}

// ====================================================================== run_sensitivity_and_risk_review

/// <summary>Portfolio Reality Check of a calculated hypothesis: concentration, risk, stress, sensitivity re-solves, costs.</summary>
public sealed class ReviewHypothesisTool(RealityCheckService realityCheck) : ResearchTool<OptimizationRun, RealityCheckDto>
{
    public override string Name => "run_sensitivity_and_risk_review";
    public override string Purpose => "Challenge a calculated portfolio: concentration, risk shares, stress correlation, in-sample history, sensitivity re-solves and costs.";
    public override ToolCategory Category => ToolCategory.Calculation;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public override void Validate(OptimizationRun run)
    {
        if (run.Status != OptimizationRunStatus.Succeeded) throw new ToolException("invalid_arguments", "Only a successful calculation can be reviewed.");
    }

    public override Task<RealityCheckDto> ExecuteAsync(ToolContext context, OptimizationRun run, CancellationToken ct)
    {
        if (run.OwnerId != context.UserId) throw new ToolException("unauthorized", "The calculation belongs to another account.");
        return realityCheck.AnalyzeAsync(run, ct);
    }

    public override string Summarize(OptimizationRun run, RealityCheckDto r) =>
        $"Review: effective holdings {r.Concentration.EffectiveHoldings:0.0}, largest {r.Concentration.LargestSymbol} {r.Concentration.LargestWeight:P0}, " +
        $"max scenario turnover {r.Scenarios.Where(s => s.Key is not ("resolved" or "equal")).Max(s => s.Turnover) ?? 0:P0}, {r.Findings.Count} findings.";
}

// ====================================================================== get_company_context

public sealed record CompanyContextDto(string Symbol, CompanyProfileDto Profile, IReadOnlyList<NewsArticleDto> RecentNews, string Note);

/// <summary>Company description and recent tagged news for one listing. Text is untrusted data: shown to the user, never executed.</summary>
public sealed class CompanyContextTool(CompanyProfileService profiles, NewsService news) : ResearchTool<string, CompanyContextDto>
{
    public override string Name => "get_company_context";
    public override string Purpose => "Company description and recent provider-tagged news for one listing.";
    public override ToolCategory Category => ToolCategory.ReadOnlyResearch;
    public override TimeSpan Timeout => TimeSpan.FromSeconds(45);

    public override void Validate(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 20) throw new ToolException("invalid_arguments", "Invalid symbol.");
    }

    public override async Task<CompanyContextDto> ExecuteAsync(ToolContext context, string symbol, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(symbol, ct);
        IReadOnlyList<NewsArticleDto> articles = [];
        try { articles = (await news.GetFeedAsync(new NewsQuery(Symbol: symbol, PageSize: 5), ct)).Items; }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* news is optional context; its failure is reported below */ }
        return new CompanyContextDto(Asset.NormalizeSymbol(symbol), profile, articles,
            "Descriptions and news are third-party text shown as data. They do not change the analysis, and news after a price move is not evidence of cause.");
    }

    public override string Summarize(string symbol, CompanyContextDto r) =>
        $"{r.Symbol}: description {r.Profile.Status}, {r.RecentNews.Count} recent tagged article(s).";
}

// ====================================================================== save_research_run

public sealed record SaveRunArgs(HypothesisConfig Config, Guid? PortfolioId, string ExpectedDataHash);

public sealed record SaveRunResult(Guid RunId, Guid PortfolioId, string PortfolioName, bool DataChanged);

/// <summary>
/// Persists a hypothesis as an optimization run in one of the caller's portfolios (re-calculated with the same
/// configuration so the saved record is complete and reproducible). Requires the user's explicit confirmation.
/// </summary>
public sealed class SaveHypothesisTool(OptimizationService optimizations, PortfolioService portfolios, PriceQueryService prices, IApplicationDbContext db,
    IAuditLog audit) : ResearchTool<SaveRunArgs, SaveRunResult>
{
    public override string Name => "save_research_run";
    public override string Purpose => "Save the chosen hypothesis as an optimization run in one of your portfolios.";
    public override ToolCategory Category => ToolCategory.UserDataChange;

    public override void Validate(SaveRunArgs args)
    {
        if (args.Config.Universe.Count < 2) throw new ToolException("invalid_arguments", "Nothing to save.");
    }

    public override async Task<SaveRunResult> ExecuteAsync(ToolContext context, SaveRunArgs args, CancellationToken ct)
    {
        var universe = args.Config.Universe.ToList();
        var currency = await db.Assets.AsNoTracking().Where(a => universe.Contains(a.Symbol)).Select(a => a.Currency).FirstAsync(ct);
        Portfolio portfolio;
        if (args.PortfolioId is { } pid) portfolio = await portfolios.LoadAsync(pid, ct); // owner-scoped: another user's id is 404
        else
        {
            var mine = await portfolios.ListAsync(ct);
            var match = mine.FirstOrDefault(p => string.Equals(p.BaseCurrency, currency, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ConflictException($"You have no portfolio in {currency}. Create one under Settings, then save again.");
            portfolio = await portfolios.LoadAsync(match.Id, ct);
        }
        var app = await prices.SettingsAsync(ct);
        var settings = new PortfolioSettings
        {
            Universe = universe,
            ExcludedSymbols = args.Config.ExcludedSymbols.ToList(),
            Objective = Enum.Parse<ObjectiveKind>(args.Config.Objective),
            CovarianceMethod = Enum.Parse<CovarianceKind>(args.Config.CovarianceMethod),
            LookbackDays = args.Config.LookbackDays,
            MaxWeight = args.Config.MaxWeight,
            CashReserve = 0,
            SectorLimits = args.Config.SectorLimits.ToDictionary(),
            RiskFreeRate = app.DefaultRiskFreeRate,
            BenchmarkSymbol = app.DefaultBenchmark,
            TransactionCostBps = args.Config.TransactionCostBps,
        };
        var (run, _) = await optimizations.EvaluateAsync(portfolio, context.UserId, settings, 30, ct);
        if (run.Status != OptimizationRunStatus.Succeeded) throw new ConflictException($"The configuration no longer produces a valid portfolio: {run.Message}");
        var changed = run.DataSnapshotHash != args.ExpectedDataHash;
        if (changed) run.Diagnostics.Add("The stored prices changed between the research evaluation and saving; this run uses the prices at saving time.");
        run.Diagnostics.Add($"Saved from research session {context.Session.Id}.");
        db.OptimizationRuns.Add(run);
        await audit.RecordAsync(AuditActions.ResearchRunSaved, "OptimizationRun", run.Id.ToString(), $"From research session {context.Session.Id}", ct: ct);
        return new SaveRunResult(run.Id, portfolio.Id, portfolio.Name, changed);
    }

    public override string Summarize(SaveRunArgs args, SaveRunResult r) =>
        $"Saved as optimization run {r.RunId} in portfolio “{r.PortfolioName}”{(r.DataChanged ? " (prices changed since evaluation)" : "")}.";
}
