using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

public class UniverseAndSymbolTests
{
    private static readonly string UniverseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "PortfolioOptimizer.Infrastructure", "Seed", "universes");

    private static readonly HashSet<string> GicsSectors =
    [
        "Communication Services", "Consumer Discretionary", "Consumer Staples", "Energy", "Financials", "Health Care", "Industrials",
        "Information Technology", "Materials", "Real Estate", "Utilities",
    ];

    private static readonly string[] NewMarkets = ["CH", "ES", "IT", "SE", "DK", "CA", "AU", "HK", "IN", "SG"];

    /// <summary>The twenty markets added 2026-10-25 and their index codes, in the order of indices.csv.</summary>
    private static readonly string[] TwentyMarketIndices =
    [
        "OBX", "OMXH25", "BEL20", "PSI", "ISEQ20", "ATX", "WIG20", "BUX", "ATHEX25", "OMXI15", "TA35", "KOSPI200", "SSE50", "KLCI", "SET50",
        "IBOV", "LQ45", "IPC", "JSE40", "NZX50",
    ];

    /// <summary>Markets of the 2026-10-25 batch with a local index ETF the price provider covers.</summary>
    private static readonly string[] TwentyMarketEtfs = ["KR", "CN", "BR", "MX", "ZA", "NZ", "PL", "TH", "MY", "ID"];

    /// <summary>No benchmark: the price provider has no history of the BUX or OMX Iceland 15 (checked 2026-10-25).</summary>
    private static readonly HashSet<string> MarketsWithoutBenchmark = ["HU", "IS"];

    [Theory]
    [InlineData("SP500", "US", 495, 510, true)]
    [InlineData("DAX40", "DE", 40, 40, true)]
    [InlineData("DAX30", "DE", 30, 30, true)]
    [InlineData("CAC40", "FR", 40, 40, true)]
    [InlineData("FTSE100", "GB", 100, 100, true)]
    [InlineData("NIKKEI225", "JP", 220, 225, true)]
    [InlineData("SMI", "CH", 20, 20, true)]
    [InlineData("IBEX35", "ES", 35, 35, true)]
    [InlineData("FTSEMIB", "IT", 40, 40, true)]
    [InlineData("OMXS30", "SE", 30, 30, true)]
    [InlineData("OMXC25", "DK", 25, 25, true)]
    [InlineData("TSX60", "CA", 60, 60, true)]
    [InlineData("ASX50", "AU", 48, 50, true)]
    [InlineData("HSI", "HK", 80, 100, true)]
    [InlineData("NIFTY50", "IN", 50, 50, true)]
    [InlineData("STI", "SG", 30, 30, false)] // the source lists no industries
    [InlineData("OBX", "NO", 24, 25, true)]
    [InlineData("OMXH25", "FI", 25, 25, true)]
    [InlineData("BEL20", "BE", 20, 20, true)] // includes Aperam, listed in Amsterdam
    [InlineData("PSI", "PT", 15, 20, true)]
    [InlineData("ISEQ20", "IE", 20, 20, false)]
    [InlineData("ATX", "AT", 20, 20, true)]
    [InlineData("WIG20", "PL", 20, 20, true)]
    [InlineData("BUX", "HU", 15, 20, true)]
    [InlineData("ATHEX25", "GR", 24, 25, true)]
    [InlineData("OMXI15", "IS", 15, 15, true)]
    [InlineData("TA35", "IL", 35, 35, true)]
    [InlineData("KOSPI200", "KR", 200, 200, true)]
    [InlineData("SSE50", "CN", 50, 50, false)]
    [InlineData("KLCI", "MY", 30, 30, true)]
    [InlineData("SET50", "TH", 50, 51, true)]
    [InlineData("IBOV", "BR", 50, 90, true)]
    [InlineData("LQ45", "ID", 44, 45, false)]
    [InlineData("IPC", "MX", 30, 36, false)]
    [InlineData("JSE40", "ZA", 39, 40, true)]
    [InlineData("NZX50", "NZ", 50, 50, true)]
    public void BundledUniverseFiles_AreWellFormed(string code, string country, int min, int max, bool hasIndustries)
    {
        var (rows, errors) = UniverseService.ParseMembers(File.ReadAllText(Path.Combine(UniverseDir, $"{code}.csv")));
        Assert.Empty(errors);
        Assert.InRange(rows.Count, min, max);
        Assert.All(rows, r => Assert.Contains(r.Sector!, GicsSectors));
        if (hasIndustries) Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Industry)));
        Assert.All(rows, r => Assert.Equal(Domain.AssetType.Equity, r.Type));
        // Every member follows its venue's symbol convention (CAC 40 includes one Amsterdam listing).
        var countries = rows.Select(r => r.Country).Distinct().ToList();
        Assert.Contains(country, countries);
        Assert.All(rows, r => Assert.Equal(r.Country, Markets.ForSymbol(r.Symbol).Country));
        Assert.All(rows, r => Assert.Equal(Markets.ListingCurrency(r.Symbol), r.Currency));
    }

    [Fact]
    public void IndexEtfFiles_ListFundsOfEachNewMarket()
    {
        foreach (var country in NewMarkets.Concat(TwentyMarketEtfs))
        {
            var (rows, errors) = UniverseService.ParseMembers(File.ReadAllText(Path.Combine(UniverseDir, $"ETF{country}.csv")));
            Assert.Empty(errors);
            Assert.InRange(rows.Count, 1, 3);
            Assert.All(rows, r => Assert.Equal(Domain.AssetType.Etf, r.Type));
            Assert.All(rows, r => Assert.Equal(country, Markets.ForSymbol(r.Symbol).Country));
            Assert.All(rows, r => Assert.Equal(Markets.ByCountry(country).Currency, r.Currency));
        }
    }

    [Fact]
    public void IndexDefinitions_ListEveryUniverseWithSourceAndRevision()
    {
        var lines = File.ReadAllLines(Path.Combine(UniverseDir, "indices.csv"));
        Assert.Equal("code,name,country,currency,benchmark,is_historical,members,source,source_url,revision,licence,verified_on,description", lines[0]);
        var codes = lines.Skip(1).Select(l => l.Split(',')[0]).ToList();
        Assert.Equal(["SP500", "DAX40", "DAX30", "CAC40", "FTSE100", "NIKKEI225", "SMI", "IBEX35", "FTSEMIB", "OMXS30", "OMXC25", "TSX60",
            "ASX50", "HSI", "NIFTY50", "STI", .. TwentyMarketIndices, .. NewMarkets.Concat(TwentyMarketEtfs).Select(c => $"ETF{c}")], codes);
        // Constituent lists come from pinned Wikipedia revisions; the ETF lists are curated and say so.
        Assert.All(lines.Skip(1).Where(l => !l.StartsWith("ETF")), l => Assert.Contains("wikipedia.org/w/index.php?oldid=", l));
        Assert.All(lines.Skip(1).Where(l => l.StartsWith("ETF")), l => Assert.Contains("Curated list", l));
        Assert.Contains(lines, l => l.StartsWith("DAX30,") && l.Contains(",true,"));
    }

    [Fact]
    public void EveryMarketHasAnIndexBenchmark_WithItsCurrency()
    {
        var lines = File.ReadAllLines(Path.Combine(UniverseDir, "indices.csv")).Skip(1).Select(l => l.Split(',')).ToList();
        foreach (var venue in Markets.Venues.Where(v => v.Country != "NL"))
        {
            var row = lines.First(l => l[2] == venue.Country);
            if (MarketsWithoutBenchmark.Contains(venue.Country)) { Assert.Equal("", row[4]); continue; } // stated, never invented
            Assert.Equal(venue.Country, Markets.ForSymbol(row[4]).Country);
            // The benchmark instrument is created on start (index level or ETF) in the market's currency.
            Assert.Contains(Infrastructure.Persistence.UniverseSeeder.Benchmarks, b => b.Symbol == row[4] && b.Currency == venue.Currency);
        }
    }

    [Theory]
    [InlineData("AAPL", "US", "USD", "AAPL", "AAPL")]
    [InlineData("BRK.B", "US", "USD", "BRK-B", "BRK.B")]
    [InlineData("SAP.DE", "DE", "EUR", "SAP.DE", "SAP.DEX")]
    [InlineData("MC.PA", "FR", "EUR", "MC.PA", "MC.PAR")]
    [InlineData("MT.AS", "NL", "EUR", "MT.AS", "MT.AMS")]
    [InlineData("TSCO.L", "GB", "GBP", "TSCO.L", "TSCO.LON")]
    [InlineData("BT-A.L", "GB", "GBP", "BT-A.L", "BT-A.LON")]
    [InlineData("7203.T", "JP", "JPY", "7203.T", null)]
    [InlineData("^GDAXI", "DE", "EUR", "^GDAXI", null)]
    [InlineData("NOVN.SW", "CH", "CHF", "NOVN.SW", null)]
    [InlineData("ITX.MC", "ES", "EUR", "ITX.MC", null)]
    [InlineData("RACE.MI", "IT", "EUR", "RACE.MI", null)]
    [InlineData("FTSEMIB.MI", "IT", "EUR", "FTSEMIB.MI", null)]
    [InlineData("ATCO-A.ST", "SE", "SEK", "ATCO-A.ST", null)]
    [InlineData("MAERSK-A.CO", "DK", "DKK", "MAERSK-A.CO", null)]
    [InlineData("RCI-B.TO", "CA", "CAD", "RCI-B.TO", "RCI-B.TRT")]
    [InlineData("BHP.AX", "AU", "AUD", "BHP.AX", null)]
    [InlineData("0005.HK", "HK", "HKD", "0005.HK", null)]
    [InlineData("M&M.NS", "IN", "INR", "M&M.NS", null)]
    [InlineData("D05.SI", "SG", "SGD", "D05.SI", null)]
    [InlineData("^HSI", "HK", "HKD", "^HSI", null)]
    [InlineData("^NSEI", "IN", "INR", "^NSEI", null)]
    [InlineData("EQNR.OL", "NO", "NOK", "EQNR.OL", null)]
    [InlineData("NOKIA.HE", "FI", "EUR", "NOKIA.HE", null)]
    [InlineData("ABI.BR", "BE", "EUR", "ABI.BR", null)]
    [InlineData("GALP.LS", "PT", "EUR", "GALP.LS", null)]
    [InlineData("RYA.IR", "IE", "EUR", "RYA.IR", null)]
    [InlineData("EBS.VI", "AT", "EUR", "EBS.VI", null)]
    [InlineData("PKO.WA", "PL", "PLN", "PKO.WA", null)]
    [InlineData("OTP.BD", "HU", "HUF", "OTP.BD", null)]
    [InlineData("ALPHA.AT", "GR", "EUR", "ALPHA.AT", null)]
    [InlineData("ARION.IC", "IS", "ISK", "ARION.IC", null)]
    [InlineData("TEVA.TA", "IL", "ILS", "TEVA.TA", null)]
    [InlineData("005930.KS", "KR", "KRW", "005930.KS", null)]
    [InlineData("600519.SS", "CN", "CNY", "600519.SS", null)]
    [InlineData("1155.KL", "MY", "MYR", "1155.KL", null)]
    [InlineData("PTT.BK", "TH", "THB", "PTT.BK", null)]
    [InlineData("PETR4.SA", "BR", "BRL", "PETR4.SA", null)]
    [InlineData("BBCA.JK", "ID", "IDR", "BBCA.JK", null)]
    [InlineData("PE&OLES.MX", "MX", "MXN", "PE&OLES.MX", null)]
    [InlineData("NPN.JO", "ZA", "ZAR", "NPN.JO", null)]
    [InlineData("FPH.NZ", "NZ", "NZD", "FPH.NZ", null)]
    [InlineData("^BVSP", "BR", "BRL", "^BVSP", null)]
    [InlineData("^J200.JO", "ZA", "ZAR", "^J200.JO", null)]
    public void SymbolMapping_PerMarket(string symbol, string country, string currency, string yahoo, string? alphaVantage)
    {
        var venue = Markets.ForSymbol(symbol);
        Assert.Equal(country, venue.Country);
        Assert.Equal(currency, venue.Currency);
        Assert.Equal(yahoo, SymbolMapper.ForYahoo(symbol));
        Assert.Equal(alphaVantage, SymbolMapper.ForAlphaVantage(symbol));
    }

    [Theory]
    [InlineData("GBp", "GBP", 100)]
    [InlineData("GBX", "GBP", 100)]
    [InlineData("GBP", "GBP", 1)]
    [InlineData("ZAc", "ZAR", 100)]
    [InlineData("ILA", "ILS", 100)]
    [InlineData("usd", "USD", 1)]
    [InlineData("JPY", "JPY", 1)]
    public void MinorCurrencyUnits_AreConvertedToIsoCurrencies(string provider, string iso, int divisor)
    {
        var (currency, d) = CurrencyUnits.Normalize(provider);
        Assert.Equal(iso, currency);
        Assert.Equal(divisor, d);
    }

    [Fact]
    public void LocalTradingDate_UsesTheExchangeTimeZone()
    {
        // 23:30 UTC on Oct 13 is already Oct 14 in Tokyo but still Oct 13 in New York.
        var instant = new DateTime(2026, 10, 13, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 14), Markets.LocalDate(Markets.ByCountry("JP"), instant));
        Assert.Equal(new DateOnly(2026, 10, 13), Markets.LocalDate(Markets.UnitedStates, instant));
    }

    [Fact]
    public void UniverseParser_RejectsSymbolsThatDoNotMatchTheirMarket()
    {
        var (rows, errors) = UniverseService.ParseMembers("symbol,name,country,currency\nSAP,SAP SE,DE,EUR\nSAP.DE,SAP SE,DE,EUR\nXX.DE,Bad,DE,EUR");
        Assert.Equal(["SAP.DE", "XX.DE"], rows.Select(r => r.Symbol));
        Assert.Contains(errors, e => e.Contains("SAP") && e.Contains("convention"));
    }

    [Fact]
    public void UniverseParser_ReadsTheOptionalTypeColumn_AndAmpersandSymbols()
    {
        var (rows, errors) = UniverseService.ParseMembers(
            "symbol,name,country,currency,type\nXIU.TO,iShares S&P/TSX 60,CA,CAD,Etf\nM&M.NS,Mahindra & Mahindra,IN,INR,\n^X.TO,Bad,CA,CAD,Index\nRY.TO,Royal Bank,CA,CAD,Bond");
        Assert.Equal([("XIU.TO", Domain.AssetType.Etf), ("M&M.NS", Domain.AssetType.Equity)], rows.Select(r => (r.Symbol, r.Type)));
        Assert.Equal(2, errors.Count(e => e.Contains("unsupported type")));
    }

    [Theory]
    [InlineData("CA", 9, 30, 16, 0)]
    [InlineData("AU", 10, 0, 16, 0)]
    [InlineData("HK", 9, 30, 16, 0)]
    [InlineData("IN", 9, 15, 15, 30)]
    [InlineData("SG", 9, 0, 17, 0)]
    [InlineData("DK", 9, 0, 17, 0)]
    [InlineData("CH", 9, 0, 17, 30)]
    [InlineData("NO", 9, 0, 16, 20)]
    [InlineData("FI", 10, 0, 18, 25)]
    [InlineData("AT", 9, 0, 17, 30)]
    [InlineData("KR", 9, 0, 15, 30)]
    [InlineData("CN", 9, 30, 15, 0)]
    [InlineData("BR", 10, 0, 17, 0)]
    [InlineData("MX", 8, 30, 15, 0)]
    [InlineData("NZ", 10, 0, 16, 45)]
    public void TradingSessions_OfTheNewMarkets(string country, int openH, int openM, int closeH, int closeM)
    {
        var (open, close) = Markets.Session(Markets.ByCountry(country), new DateOnly(2026, 10, 14));
        Assert.Equal(new TimeOnly(openH, openM), open);
        Assert.Equal(new TimeOnly(closeH, closeM), close);
    }

    [Fact]
    public void ForeignCurrencyListings_AreRecordedTheSameWayInTheUniverseFiles()
    {
        // Graphisoft Park trades in euros in Budapest; Hammerson's Dublin line in pounds (price provider, 2026-10-25).
        Assert.Equal("EUR", Markets.ListingCurrency("GSPARK.BD"));
        Assert.Equal("GBP", Markets.ListingCurrency("HMSO.IR"));
        Assert.Equal("HUF", Markets.ListingCurrency("OTP.BD"));
    }

    [Fact]
    public void EveryVenueHasAnIanaTimeZone_KnownToThisSystem()
    {
        Assert.All(Markets.Venues, v => Assert.NotEqual(TimeZoneInfo.Utc, Markets.TimeZone(v)));
    }
}
