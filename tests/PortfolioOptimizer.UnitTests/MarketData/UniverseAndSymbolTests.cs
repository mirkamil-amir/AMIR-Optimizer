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

    [Theory]
    [InlineData("SP500", "US", 495, 510)]
    [InlineData("DAX40", "DE", 40, 40)]
    [InlineData("DAX30", "DE", 30, 30)]
    [InlineData("CAC40", "FR", 40, 40)]
    [InlineData("FTSE100", "GB", 100, 100)]
    [InlineData("NIKKEI225", "JP", 220, 225)]
    public void BundledUniverseFiles_AreWellFormed(string code, string country, int min, int max)
    {
        var (rows, errors) = UniverseService.ParseMembers(File.ReadAllText(Path.Combine(UniverseDir, $"{code}.csv")));
        Assert.Empty(errors);
        Assert.InRange(rows.Count, min, max);
        Assert.All(rows, r => Assert.Contains(r.Sector!, GicsSectors));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Industry)));
        // Every member follows its venue's symbol convention (CAC 40 includes one Amsterdam listing).
        var countries = rows.Select(r => r.Country).Distinct().ToList();
        Assert.Contains(country, countries);
        Assert.All(rows, r => Assert.Equal(r.Country, Markets.ForSymbol(r.Symbol).Country));
        Assert.All(rows, r => Assert.Equal(Markets.ByCountry(r.Country).Currency, r.Currency));
    }

    [Fact]
    public void IndexDefinitions_ListEveryUniverseWithSourceAndRevision()
    {
        var lines = File.ReadAllLines(Path.Combine(UniverseDir, "indices.csv"));
        Assert.Equal("code,name,country,currency,benchmark,is_historical,members,source,source_url,revision,licence,verified_on,description", lines[0]);
        var codes = lines.Skip(1).Select(l => l.Split(',')[0]).ToList();
        Assert.Equal(["SP500", "DAX40", "DAX30", "CAC40", "FTSE100", "NIKKEI225"], codes);
        Assert.All(lines.Skip(1), l => Assert.Contains("wikipedia.org/w/index.php?oldid=", l));
        Assert.Contains(lines, l => l.StartsWith("DAX30,") && l.Contains(",true,"));
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
}
