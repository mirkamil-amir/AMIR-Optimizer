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
/// 7203.T, MT.AS, NOVN.SW, ATCO-A.ST, RCI-B.TO, 0005.HK, M&amp;M.NS, 005930.KS, 600519.SS, PETR4.SA, NAFTRACISHRS.MX). Providers translate
/// canonical symbols with <see cref="SymbolMapper"/>.
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
        // Twenty markets added 2026-10-25.
        new("NO", "Norway", "Oslo Børs", "XOSL", "NOK", ".OL", null, "Europe/Oslo"),
        new("FI", "Finland", "Nasdaq Helsinki", "XHEL", "EUR", ".HE", null, "Europe/Helsinki"),
        new("BE", "Belgium", "Euronext Brussels", "XBRU", "EUR", ".BR", null, "Europe/Brussels"),
        new("PT", "Portugal", "Euronext Lisbon", "XLIS", "EUR", ".LS", null, "Europe/Lisbon"),
        new("IE", "Ireland", "Euronext Dublin", "XDUB", "EUR", ".IR", null, "Europe/Dublin"),
        new("AT", "Austria", "Wiener Börse", "XWBO", "EUR", ".VI", null, "Europe/Vienna"),
        new("PL", "Poland", "Warsaw Stock Exchange", "XWAR", "PLN", ".WA", null, "Europe/Warsaw"),
        new("HU", "Hungary", "Budapest Stock Exchange", "XBUD", "HUF", ".BD", null, "Europe/Budapest"),
        new("GR", "Greece", "Athens Stock Exchange", "ASEX", "EUR", ".AT", null, "Europe/Athens"),
        new("IS", "Iceland", "Nasdaq Iceland", "XICE", "ISK", ".IC", null, "Atlantic/Reykjavik"),
        new("IL", "Israel", "Tel Aviv Stock Exchange", "XTAE", "ILS", ".TA", null, "Asia/Jerusalem"),
        new("KR", "South Korea", "Korea Exchange", "XKRX", "KRW", ".KS", null, "Asia/Seoul"),
        new("CN", "China", "Shanghai Stock Exchange", "XSHG", "CNY", ".SS", null, "Asia/Shanghai"),
        new("MY", "Malaysia", "Bursa Malaysia", "XKLS", "MYR", ".KL", null, "Asia/Kuala_Lumpur"),
        new("TH", "Thailand", "Stock Exchange of Thailand", "XBKK", "THB", ".BK", null, "Asia/Bangkok"),
        new("BR", "Brazil", "B3", "BVMF", "BRL", ".SA", null, "America/Sao_Paulo"),
        new("ID", "Indonesia", "Indonesia Stock Exchange", "XIDX", "IDR", ".JK", null, "Asia/Jakarta"),
        new("MX", "Mexico", "Bolsa Mexicana de Valores", "XMEX", "MXN", ".MX", null, "America/Mexico_City"),
        new("ZA", "South Africa", "Johannesburg Stock Exchange", "XJSE", "ZAR", ".JO", null, "Africa/Johannesburg"),
        new("NZ", "New Zealand", "New Zealand Exchange", "XNZE", "NZD", ".NZ", null, "Pacific/Auckland"),
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
    /// Listings that trade in another currency than their venue's (checked against the price provider): the Jardine group
    /// companies trade in US dollars on the Singapore Exchange (2026-10-24), Graphisoft Park in euros in Budapest and
    /// Hammerson's Dublin line in pounds (2026-10-25). The universe files carry the same currency.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ForeignCurrencyListings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["D01.SI"] = "USD", ["H78.SI"] = "USD", ["J36.SI"] = "USD", ["GSPARK.BD"] = "EUR", ["HMSO.IR"] = "GBP",
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
        ["^OMXH25"] = "FI", ["^BFX"] = "BE", ["^ISEQ"] = "IE", ["^ATX"] = "AT", ["^KS11"] = "KR", ["^KLSE"] = "MY", ["^BVSP"] = "BR",
        ["^JKLQ45"] = "ID", ["^MXX"] = "MX", ["^J200.JO"] = "ZA", ["^NZ50"] = "NZ",
        // OSEBX.OL, PSI20.LS, GD.AT, TA35.TA and 000001.SS carry their venue's suffix; Poland and Thailand use index ETFs.
    };

    public static TimeZoneInfo TimeZone(MarketVenue venue)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(venue.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Regular continuous-trading session in exchange-local time (no auctions, lunch breaks, half days, weekday differences
    /// or holiday calendar – an approximation used to label quotes). Tokyo closes at 15:30 since the session extension of
    /// 5 Nov 2024; earlier dates closed at 15:00.
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
        "NO" => (new(9, 0), new(16, 20)),
        "FI" => (new(10, 0), new(18, 25)),
        "PT" or "IE" => (new(8, 0), new(16, 30)),
        "PL" => (new(9, 0), new(16, 50)),
        "HU" or "ZA" => (new(9, 0), new(17, 0)),
        "GR" => (new(10, 0), new(17, 20)),
        "IS" => (new(9, 30), new(15, 30)),
        "IL" => (new(10, 0), new(17, 15)),
        "KR" => (new(9, 0), new(15, 30)),
        "CN" => (new(9, 30), new(15, 0)),
        "MY" => (new(9, 0), new(16, 45)),
        "TH" => (new(10, 0), new(16, 30)),
        "BR" => (new(10, 0), new(17, 0)),
        "ID" => (new(9, 0), new(16, 0)),
        "MX" => (new(8, 30), new(15, 0)),
        "NZ" => (new(10, 0), new(16, 45)),
        _ => (new(9, 0), new(17, 30)), // Xetra, Euronext Paris/Amsterdam/Brussels, SIX, Madrid, Milan, Stockholm, Vienna
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
