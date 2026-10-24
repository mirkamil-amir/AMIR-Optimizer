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
/// 7203.T, MT.AS, NOVN.SW, ATCO-A.ST, RCI-B.TO, 0005.HK, M&amp;M.NS). Providers translate canonical symbols with <see cref="SymbolMapper"/>.
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
        new("CH", "Switzerland", "SIX Swiss Exchange", "XSWX", "CHF", ".SW", null, "Europe/Zurich"),
        new("ES", "Spain", "Bolsa de Madrid", "XMAD", "EUR", ".MC", null, "Europe/Madrid"),
        new("IT", "Italy", "Borsa Italiana", "XMIL", "EUR", ".MI", null, "Europe/Rome"),
        new("SE", "Sweden", "Nasdaq Stockholm", "XSTO", "SEK", ".ST", null, "Europe/Stockholm"),
        new("DK", "Denmark", "Nasdaq Copenhagen", "XCSE", "DKK", ".CO", null, "Europe/Copenhagen"),
        new("CA", "Canada", "Toronto Stock Exchange", "XTSE", "CAD", ".TO", ".TRT", "America/Toronto"),
        new("AU", "Australia", "Australian Securities Exchange", "XASX", "AUD", ".AX", null, "Australia/Sydney"),
        new("HK", "Hong Kong", "Hong Kong Stock Exchange", "XHKG", "HKD", ".HK", null, "Asia/Hong_Kong"),
        new("IN", "India", "National Stock Exchange of India", "XNSE", "INR", ".NS", null, "Asia/Kolkata"),
        new("SG", "Singapore", "Singapore Exchange", "XSES", "SGD", ".SI", null, "Asia/Singapore"),
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

    /// <summary>
    /// Listings that trade in another currency than their venue's (checked against the price provider 2026-10-24): the
    /// Jardine group companies trade in US dollars on the Singapore Exchange. The universe files carry the same currency.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ForeignCurrencyListings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["D01.SI"] = "USD", ["H78.SI"] = "USD", ["J36.SI"] = "USD",
    };

    /// <summary>Trading currency of a canonical symbol: its venue's currency unless listed in <see cref="ForeignCurrencyListings"/>.</summary>
    public static string ListingCurrency(string symbol) =>
        ForeignCurrencyListings.TryGetValue(symbol, out var currency) ? currency : ForSymbol(symbol).Currency;

    public static MarketVenue ByCountry(string country) =>
        Venues.FirstOrDefault(v => v.Country == country) ?? throw new ArgumentException($"Unsupported market '{country}'.", nameof(country));

    /// <summary>Benchmark index symbols (Yahoo) and the market they belong to.</summary>
    public static readonly IReadOnlyDictionary<string, string> IndexVenues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["^GSPC"] = "US", ["^GDAXI"] = "DE", ["^FCHI"] = "FR", ["^FTSE"] = "GB", ["^N225"] = "JP",
        ["^SSMI"] = "CH", ["^IBEX"] = "ES", ["^OMX"] = "SE", ["^OMXC25"] = "DK", ["^GSPTSE"] = "CA", ["^AXJO"] = "AU",
        ["^HSI"] = "HK", ["^NSEI"] = "IN", ["^STI"] = "SG", // the FTSE MIB index (FTSEMIB.MI) carries the Milan suffix
    };

    public static TimeZoneInfo TimeZone(MarketVenue venue)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(venue.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Regular continuous-trading session in exchange-local time (no auctions, lunch breaks, half days or holiday calendar).
    /// Tokyo closes at 15:30 since the session extension of 5 Nov 2024; earlier dates closed at 15:00.
    /// </summary>
    public static (TimeOnly Open, TimeOnly Close) Session(MarketVenue venue, DateOnly date) => venue.Country switch
    {
        "US" or "CA" => (new(9, 30), new(16, 0)),
        "GB" => (new(8, 0), new(16, 30)),
        "JP" => (new(9, 0), date < new DateOnly(2024, 11, 5) ? new(15, 0) : new(15, 30)),
        "DK" => (new(9, 0), new(17, 0)),
        "AU" => (new(10, 0), new(16, 0)),
        "HK" => (new(9, 30), new(16, 0)),
        "IN" => (new(9, 15), new(15, 30)),
        "SG" => (new(9, 0), new(17, 0)),
        _ => (new(9, 0), new(17, 30)), // Xetra, Euronext Paris/Amsterdam, SIX, Madrid, Milan, Stockholm
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

    /// <summary>Alpha Vantage: TSCO.L → TSCO.LON, SAP.DE → SAP.DEX, MC.PA → MC.PAR, RY.TO → RY.TRT; null when the venue is not
    /// covered (Tokyo and the other venues without a suffix in <see cref="Markets.Venues"/>, indices).</summary>
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
