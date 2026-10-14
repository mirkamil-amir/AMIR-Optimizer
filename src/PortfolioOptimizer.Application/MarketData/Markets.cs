namespace PortfolioOptimizer.Application.MarketData;

/// <summary>A trading venue the catalogue supports, with the conventions providers use for it.</summary>
/// <param name="Country">ISO 3166 country of the venue.</param>
/// <param name="Suffix">Suffix of canonical symbols on this venue (Yahoo convention); empty for US listings.</param>
/// <param name="AlphaVantageSuffix">Alpha Vantage suffix, or null when Alpha Vantage does not cover the venue.</param>
/// <param name="TimeZoneId">IANA time zone of the exchange (local trading dates).</param>
public sealed record MarketVenue(
    string Country,
    string CountryName,
    string Exchange,
    string Mic,
    string Currency,
    string Suffix,
    string? AlphaVantageSuffix,
    string TimeZoneId);

/// <summary>
/// Supported venues and the canonical symbol convention. Canonical symbols follow Yahoo Finance: US tickers without a
/// suffix (share classes with '-', e.g. BRK-B), other venues with an exchange suffix (SAP.DE, MC.PA, TSCO.L, BT-A.L,
/// 7203.T, MT.AS). Providers translate canonical symbols with <see cref="SymbolMapper"/>.
/// </summary>
public static class Markets
{
    public static readonly MarketVenue UnitedStates = new("US", "United States", "NYSE / NASDAQ", "XNYS", "USD", "", "", "America/New_York");

    public static readonly IReadOnlyList<MarketVenue> Venues =
    [
        UnitedStates,
        new("DE", "Germany", "XETRA", "XETR", "EUR", ".DE", ".DEX", "Europe/Berlin"),
        new("FR", "France", "Euronext Paris", "XPAR", "EUR", ".PA", ".PAR", "Europe/Paris"),
        new("NL", "Netherlands", "Euronext Amsterdam", "XAMS", "EUR", ".AS", ".AMS", "Europe/Amsterdam"),
        new("GB", "United Kingdom", "London Stock Exchange", "XLON", "GBP", ".L", ".LON", "Europe/London"),
        new("JP", "Japan", "Tokyo Stock Exchange", "XTKS", "JPY", ".T", null, "Asia/Tokyo"),
    ];

    /// <summary>The venue of a canonical symbol (index symbols such as ^GDAXI are matched by <see cref="IndexVenues"/>).</summary>
    public static MarketVenue ForSymbol(string symbol)
    {
        if (IndexVenues.TryGetValue(symbol, out var country)) return ByCountry(country);
        var dot = symbol.LastIndexOf('.');
        if (dot > 0)
        {
            var suffix = symbol[dot..];
            foreach (var venue in Venues)
                if (venue.Suffix.Length > 0 && string.Equals(venue.Suffix, suffix, StringComparison.OrdinalIgnoreCase)) return venue;
        }
        return UnitedStates;
    }

    public static MarketVenue ByCountry(string country) =>
        Venues.FirstOrDefault(v => v.Country == country) ?? throw new ArgumentException($"Unsupported market '{country}'.", nameof(country));

    /// <summary>Benchmark index symbols (Yahoo) and the market they belong to.</summary>
    public static readonly IReadOnlyDictionary<string, string> IndexVenues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["^GSPC"] = "US", ["^GDAXI"] = "DE", ["^FCHI"] = "FR", ["^FTSE"] = "GB", ["^N225"] = "JP",
    };

    public static TimeZoneInfo TimeZone(MarketVenue venue)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(venue.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Regular continuous-trading session in exchange-local time (no auctions, half days or holiday calendar). Tokyo closes
    /// at 15:30 since the session extension of 5 Nov 2024; earlier dates closed at 15:00.
    /// </summary>
    public static (TimeOnly Open, TimeOnly Close) Session(MarketVenue venue, DateOnly date) => venue.Country switch
    {
        "US" => (new(9, 30), new(16, 0)),
        "GB" => (new(8, 0), new(16, 30)),
        "JP" => (new(9, 0), date < new DateOnly(2024, 11, 5) ? new(15, 0) : new(15, 30)),
        _ => (new(9, 0), new(17, 30)), // Xetra, Euronext Paris/Amsterdam
    };

    /// <summary>UTC instant of a local exchange time on a date (daylight saving handled by the venue's time zone).</summary>
    public static DateTime ToUtc(MarketVenue venue, DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified), TimeZone(venue));

    /// <summary>The exchange-local calendar date of an instant.</summary>
    public static DateOnly LocalDate(MarketVenue venue, DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone(venue)));
}

/// <summary>Translates canonical symbols to provider-specific symbols.</summary>
public static class SymbolMapper
{
    /// <summary>Yahoo: canonical symbols already are Yahoo symbols; a US share class typed with '.' is translated (BRK.B → BRK-B).</summary>
    public static string ForYahoo(string symbol)
    {
        if (symbol.StartsWith('^')) return symbol;
        var venue = Markets.ForSymbol(symbol);
        return venue.Suffix.Length == 0 ? symbol.Replace('.', '-') : symbol;
    }

    /// <summary>Alpha Vantage: TSCO.L → TSCO.LON, SAP.DE → SAP.DEX, MC.PA → MC.PAR; null when the venue is not covered (Tokyo, indices).</summary>
    public static string? ForAlphaVantage(string symbol)
    {
        if (symbol.StartsWith('^')) return null;
        var venue = Markets.ForSymbol(symbol);
        if (venue.AlphaVantageSuffix is null) return null;
        return venue.Suffix.Length == 0 ? symbol : symbol[..^venue.Suffix.Length] + venue.AlphaVantageSuffix;
    }
}

/// <summary>
/// Normalizes provider price units to ISO currencies. London prices are quoted in pence (GBp/GBX), Johannesburg in cents
/// (ZAc) and Tel Aviv in agorot (ILA); they are divided into the major unit so stored prices are always in the ISO currency.
/// </summary>
public static class CurrencyUnits
{
    public static (string Currency, decimal Divisor) Normalize(string providerCurrency) => providerCurrency switch
    {
        "GBp" or "GBX" or "GBx" => ("GBP", 100m),
        "ZAc" or "ZAC" => ("ZAR", 100m),
        "ILA" or "ILa" => ("ILS", 100m),
        _ => (providerCurrency.ToUpperInvariant(), 1m),
    };
}
