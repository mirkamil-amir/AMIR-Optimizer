namespace PortfolioOptimizer.Application.News;

/// <summary>Why a company is in the scheduled set; lower value = fetched first.</summary>
public enum ScheduledCompanySource { Held = 0, Watched = 1, Curated = 2 }

public sealed record ScheduledCompany(string Symbol, string? Name, ScheduledCompanySource Source);

/// <summary>Result of one scheduler run (logged; no user data).</summary>
public sealed record NewsScheduleRun(int Companies, int Due);

/// <summary>
/// Which companies a scheduled run fetches (pure, unit-tested): every listing held in any portfolio, on any watchlist or in
/// the curated list, each once under its highest priority (held, then watched, then curated); of those, the ones due at a
/// supporting provider, stalest first within a priority, at most <c>budget</c>.
/// </summary>
public static class NewsSchedule
{
    public static IReadOnlyList<ScheduledCompany> Merge(IEnumerable<ScheduledCompany> candidates) =>
        candidates.GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(c => c.Source).First())
            .OrderBy(c => c.Source).ThenBy(c => c.Symbol, StringComparer.Ordinal)
            .ToList();

    /// <param name="lastAttempt">Oldest last attempt over the providers that support the symbol; null = never fetched.</param>
    /// <param name="isDue">Whether any supporting provider is due for the symbol.</param>
    public static IReadOnlyList<ScheduledCompany> SelectDue(IReadOnlyList<ScheduledCompany> merged, Func<string, bool> isDue,
        Func<string, DateTime?> lastAttempt, int budget) =>
        merged.Where(c => isDue(c.Symbol))
            .OrderBy(c => c.Source).ThenBy(c => lastAttempt(c.Symbol) ?? DateTime.MinValue).ThenBy(c => c.Symbol, StringComparer.Ordinal)
            .Take(Math.Max(0, budget))
            .ToList();
}
