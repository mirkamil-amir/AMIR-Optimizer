namespace PortfolioOptimizer.Application.Fx;

/// <summary>
/// A converted rate with its provenance. <see cref="Rate"/> follows the single internal convention:
/// <b>units of <see cref="To"/> per one unit of <see cref="From"/></b>, so <c>amount_to = amount_from × Rate</c>.
/// </summary>
/// <param name="ObservationDate">Date of the oldest observation used (the two legs of a cross rate can differ).</param>
/// <param name="CarriedForwardDays">Calendar days between the requested date and <paramref name="ObservationDate"/> (weekends, holidays).</param>
public sealed record FxQuote(string From, string To, double Rate, DateOnly RequestedDate, DateOnly ObservationDate, int CarriedForwardDays,
    string Source, bool IsSimulated)
{
    public bool IsIdentity => From == To;
}

/// <summary>Rates booked on trades are stored with 10 decimals (numeric(20,10)); amounts are computed from the rounded rate.</summary>
public static class FxPrecision
{
    public const int Decimals = 10;
    public static decimal Round(double rate) => Math.Round((decimal)rate, Decimals);
}

/// <summary>Why no rate could be given; analyses stop or exclude the affected data instead of substituting a rate.</summary>
public sealed class FxRateUnavailableException(string from, string to, DateOnly date, string reason)
    : Exception($"No {from}→{to} exchange rate for {date:yyyy-MM-dd}: {reason}")
{
    public string From { get; } = from;
    public string To { get; } = to;
    public DateOnly Date { get; } = date;
    public string Reason { get; } = reason;
}

/// <summary>
/// In-memory daily FX observations of one source, all quoted as units per 1 EUR (the pivot). Pure and deterministic.
///
/// Policy (docs/currency.md):
/// * <c>Rate(A, B, d) = unitsPerEur(B, d) / unitsPerEur(A, d)</c> – direct, inverse and cross rates come from the same
///   observations, so <c>Rate(A,B,d) × Rate(B,A,d) = 1</c> and <c>Rate(A,B,d) × Rate(B,C,d) = Rate(A,C,d)</c> exactly.
/// * For a date without an observation (weekend, holiday, before publication) the latest earlier observation is used if it
///   is at most <see cref="MaxCarryForwardDays"/> calendar days old; otherwise the rate is unavailable. Never 1.0.
/// * The identity A→A is 1 by definition (not a fallback).
/// </summary>
public sealed class FxTable
{
    public const string Pivot = "EUR";
    /// <summary>Covers a weekend plus one holiday (e.g. Thursday → Monday over Easter).</summary>
    public const int DefaultMaxCarryForwardDays = 4;

    private readonly Dictionary<string, (DateOnly[] Dates, double[] Values)> _series;

    public FxTable(string source, bool isSimulated, IEnumerable<(string Currency, DateOnly Date, double UnitsPerEur)> observations,
        int maxCarryForwardDays = DefaultMaxCarryForwardDays)
    {
        Source = source;
        IsSimulated = isSimulated;
        MaxCarryForwardDays = maxCarryForwardDays;
        _series = observations
            .Where(o => double.IsFinite(o.UnitsPerEur) && o.UnitsPerEur > 0 && o.Currency != Pivot)
            .GroupBy(o => o.Currency)
            .ToDictionary(g => g.Key, g =>
            {
                var ordered = g.GroupBy(o => o.Date).Select(d => d.Last()).OrderBy(o => o.Date).ToArray();
                return (ordered.Select(o => o.Date).ToArray(), ordered.Select(o => o.UnitsPerEur).ToArray());
            });
    }

    public string Source { get; }
    public bool IsSimulated { get; }
    public int MaxCarryForwardDays { get; }
    public IReadOnlyCollection<string> Currencies => [Pivot, .. _series.Keys];

    /// <summary>Latest observation date of <paramref name="currency"/> (today for EUR), or null without data.</summary>
    public DateOnly? LastObservation(string currency) =>
        currency == Pivot ? null : _series.TryGetValue(currency, out var s) && s.Dates.Length > 0 ? s.Dates[^1] : null;

    /// <summary>
    /// Daily rates from → to on the days in [start, end] on which every non-EUR leg has its own observation (no carried-forward
    /// values – charts show only published rates).
    /// </summary>
    public IReadOnlyList<(DateOnly Date, double Rate)> ObservedSeries(string from, string to, DateOnly start, DateOnly end)
    {
        if (from == to) return [];
        var legs = new[] { from, to }.Where(c => c != Pivot).Distinct().ToList();
        if (legs.Any(c => !_series.ContainsKey(c))) return [];
        var dates = legs.Select(c => _series[c].Dates.Where(d => d >= start && d <= end).ToHashSet())
            .Aggregate((a, b) => { a.IntersectWith(b); return a; })
            .Order();
        var result = new List<(DateOnly, double)>();
        foreach (var d in dates)
            if (TryRate(from, to, d, out var q, out _) && q!.CarriedForwardDays == 0) result.Add((d, q.Rate));
        return result;
    }

    public FxQuote Rate(string from, string to, DateOnly date)
    {
        if (TryRate(from, to, date, out var quote, out var reason)) return quote!;
        throw new FxRateUnavailableException(from, to, date, reason!);
    }

    public bool TryRate(string from, string to, DateOnly date, out FxQuote? quote, out string? reason)
    {
        quote = null;
        if (from == to)
        {
            reason = null;
            quote = new FxQuote(from, to, 1.0, date, date, 0, Source, IsSimulated);
            return true;
        }
        if (!Leg(from, date, out var fromPerEur, out var fromDate, out reason) || !Leg(to, date, out var toPerEur, out var toDate, out reason))
            return false;
        var observed = fromDate < toDate ? fromDate : toDate;
        var rate = toPerEur / fromPerEur;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            reason = "the stored observations give an invalid rate";
            return false;
        }
        quote = new FxQuote(from, to, rate, date, observed, date.DayNumber - observed.DayNumber, Source, IsSimulated);
        return true;
    }

    private bool Leg(string currency, DateOnly date, out double perEur, out DateOnly observed, out string? reason)
    {
        perEur = 1;
        observed = date;
        reason = null;
        if (currency == Pivot) return true;
        if (!_series.TryGetValue(currency, out var s) || s.Dates.Length == 0)
        {
            reason = $"no {currency} reference rates are available for this period";
            return false;
        }
        var i = Array.BinarySearch(s.Dates, date);
        if (i < 0) i = ~i - 1;   // latest observation on or before the date
        if (i < 0)
        {
            reason = $"the first stored {currency} rate is from {s.Dates[0]:yyyy-MM-dd}";
            return false;
        }
        var gap = date.DayNumber - s.Dates[i].DayNumber;
        if (gap > MaxCarryForwardDays)
        {
            reason = $"the latest {currency} rate on or before that date is from {s.Dates[i]:yyyy-MM-dd} ({gap} days earlier; at most {MaxCarryForwardDays} are carried forward)";
            return false;
        }
        perEur = s.Values[i];
        observed = s.Dates[i];
        return true;
    }
}
