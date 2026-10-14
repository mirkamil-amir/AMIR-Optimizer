using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.MarketData;

public class CatalogueParsingTests
{
    [Fact]
    public void Parse_HandlesQuotedFieldsCommentsAndDefaults()
    {
        const string csv = """"
            # comment line
            symbol,name,sector,industry,exchange,currency,type
            aapl,Apple Inc.,Information Technology,"Technology Hardware, Storage & Peripherals",NASDAQ,USD,Equity
            BRK-B,"Berkshire ""B""",Financials,Multi-Sector Holdings,NYSE,,
            SPY,SPDR S&P 500 ETF Trust,,,NYSE Arca,USD,Etf
            """";
        var (rows, errors) = CatalogueService.Parse(csv);
        Assert.Empty(errors);
        Assert.Equal(3, rows.Count);
        Assert.Equal("AAPL", rows[0].Symbol);
        Assert.Equal("Technology Hardware, Storage & Peripherals", rows[0].Industry);
        Assert.Equal("Berkshire \"B\"", rows[1].Name);
        Assert.Equal("USD", rows[1].Currency);
        Assert.Equal(AssetType.Equity, rows[1].Type);
        Assert.Null(rows[2].Sector);
        Assert.Equal(AssetType.Etf, rows[2].Type);
    }

    [Fact]
    public void Parse_ReportsInvalidRows_WithoutGuessingValues()
    {
        const string csv = """
            symbol,name,sector
            ,No symbol,Energy
            XOM,,Energy
            BAD SYMBOL,Spaces,Energy
            CVX,Chevron,Energy
            CVX,Chevron again,Energy
            """;
        var (rows, errors) = CatalogueService.Parse(csv);
        Assert.Equal(["CVX"], rows.Select(r => r.Symbol));
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("has no name"));
        Assert.Contains(errors, e => e.Contains("duplicate"));
    }

    [Fact]
    public void Parse_RequiresHeaderColumns()
    {
        var (rows, errors) = CatalogueService.Parse("ticker,company\nAAPL,Apple");
        Assert.Empty(rows);
        Assert.Contains("Missing column", Assert.Single(errors));
    }
}
