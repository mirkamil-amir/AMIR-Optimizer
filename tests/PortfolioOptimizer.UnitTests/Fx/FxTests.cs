using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Infrastructure.Fx;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Fx;

public sealed class FxTableTests
{
    // Fixture: units per 1 EUR on Mon 2026-10-12 … Wed 2026-10-14 (Fri 2026-10-09 for the weekend case).
    private static readonly DateOnly Fri = new(2026, 10, 9), Mon = new(2026, 10, 12), Tue = new(2026, 10, 13), Wed = new(2026, 10, 14);

    private static FxTable Table() => new("TEST", false,
    [
        ("USD", Fri, 1.10), ("USD", Mon, 1.12), ("USD", Tue, 1.15), ("USD", Wed, 1.16),
        ("JPY", Fri, 160.0), ("JPY", Mon, 161.0), ("JPY", Tue, 162.0),
        ("GBP", Mon, 0.85), ("GBP", Tue, 0.86),
        ("CHF", Mon, 0.93), ("CHF", Tue, 0.94),
    ]);

    [Fact]
    public void Convention_IsTargetUnitsPerSourceUnit()
    {
        var t = Table();
        // EUR→USD 1.15 means 1 EUR buys 1.15 USD: €100 → $115.
        Assert.Equal(1.15, t.Rate("EUR", "USD", Tue).Rate, 12);
        Assert.Equal(115.0, 100 * t.Rate("EUR", "USD", Tue).Rate, 9);
        // USD→EUR is the reciprocal of the same observation.
        Assert.Equal(1 / 1.15, t.Rate("USD", "EUR", Tue).Rate, 12);
    }

    [Theory]
    [InlineData("USD", "EUR")]
    [InlineData("JPY", "GBP")]
    [InlineData("CHF", "USD")]
    [InlineData("GBP", "JPY")]
    public void DirectAndInverse_AreConsistent(string a, string b)
    {
        var t = Table();
        Assert.Equal(1.0, t.Rate(a, b, Tue).Rate * t.Rate(b, a, Tue).Rate, 12);
    }

    [Fact]
    public void CrossRates_GoThroughTheEuro_AndChainExactly()
    {
        var t = Table();
        // USD→JPY on Tuesday = (JPY per EUR) / (USD per EUR) = 162 / 1.15.
        Assert.Equal(162.0 / 1.15, t.Rate("USD", "JPY", Tue).Rate, 10);
        Assert.Equal(t.Rate("USD", "JPY", Tue).Rate, t.Rate("USD", "GBP", Tue).Rate * t.Rate("GBP", "JPY", Tue).Rate, 10);
        Assert.Equal(0.94 / 0.86, t.Rate("GBP", "CHF", Tue).Rate, 12);
    }

    [Fact]
    public void Weekend_UsesThePreviousObservation_AndSaysSo()
    {
        var q = Table().Rate("EUR", "USD", new DateOnly(2026, 10, 11));   // Sunday
        Assert.Equal(1.10, q.Rate, 12);
        Assert.Equal(Fri, q.ObservationDate);
        Assert.Equal(2, q.CarriedForwardDays);
    }

    [Fact]
    public void MissingOrTooOldRates_AreUnavailable_NeverOne()
    {
        var t = Table();
        // GBP has nothing before Monday; Saturday cannot use a later rate.
        Assert.False(t.TryRate("GBP", "EUR", new DateOnly(2026, 10, 10), out var q, out var reason));
        Assert.Null(q);
        Assert.Contains("first stored GBP rate", reason);
        // JPY stops on Tuesday: Monday next week is beyond the carry-forward window.
        var ex = Assert.Throws<FxRateUnavailableException>(() => t.Rate("JPY", "USD", new DateOnly(2026, 10, 19)));
        Assert.Contains("at most 4 are carried forward", ex.Message);
        // A currency with no data at all.
        var missing = Assert.Throws<FxRateUnavailableException>(() => t.Rate("SEK", "EUR", Tue)).Message;
        Assert.Contains("no SEK reference rates are available", missing);
        Assert.DoesNotContain("ECB", missing);
    }

    [Fact]
    public void CrossRate_ReportsTheOlderLeg()
    {
        var q = Table().Rate("USD", "JPY", Wed);   // USD observed Wednesday, JPY carried from Tuesday
        Assert.Equal(Tue, q.ObservationDate);
        Assert.Equal(162.0 / 1.16, q.Rate, 10);
    }

    [Fact]
    public void InvalidObservations_AreIgnored()
    {
        var t = new FxTable("TEST", false, [("USD", Mon, 0), ("USD", Tue, double.NaN), ("USD", Fri, -1)]);
        Assert.False(t.TryRate("USD", "EUR", Tue, out _, out _));
    }

    [Fact]
    public void ObservedSeries_ContainsOnlyPublishedDays()
    {
        // USD has Fri/Mon/Tue/Wed, JPY Fri/Mon/Tue: USD→JPY exists only where both legs were published.
        var s = Table().ObservedSeries("USD", "JPY", Fri, Wed);
        Assert.Equal([Fri, Mon, Tue], s.Select(p => p.Date));
        Assert.Equal(162.0 / 1.15, s[^1].Rate, 10);
        Assert.Equal([Fri, Mon, Tue, Wed], Table().ObservedSeries("EUR", "USD", Fri, Wed).Select(p => p.Date));
        Assert.Empty(Table().ObservedSeries("USD", "USD", Fri, Wed));
        Assert.Empty(Table().ObservedSeries("SEK", "EUR", Fri, Wed));
    }

    [Fact]
    public void Identity_IsOneByDefinition()
    {
        var q = new FxTable("TEST", false, []).Rate("CHF", "CHF", Tue);
        Assert.Equal(1.0, q.Rate);
        Assert.True(q.IsIdentity);
    }
}

public sealed class CurrencyConverterTests
{
    private static readonly DateOnly D1 = new(2026, 10, 12), D2 = new(2026, 10, 13), D3 = new(2026, 10, 14);

    private static PriceSeries Series(string symbol, params (DateOnly, double)[] points) =>
        new(symbol, 1, points.Select(p => new PricePoint(p.Item1, p.Item2)).ToList(), true, false);

    private static readonly FxTable Fx = new("TEST", false, [("USD", D1, 1.10), ("USD", D2, 1.00), ("USD", D3, 1.25), ("JPY", D1, 160), ("JPY", D2, 160), ("JPY", D3, 150)]);

    [Fact]
    public void UsdStock_InEurPortfolio_UsesTheRateOfEachDate_NotTodays()
    {
        // AAPL $110 → $110 → $125; EUR per USD = 1/1.10, 1/1.00, 1/1.25.
        var c = CurrencyConverter.Convert(Series("AAPL", (D1, 110), (D2, 110), (D3, 125)), "USD", "EUR", Fx);
        Assert.Equal([100.0, 110.0, 100.0], c.Series.Points.Select(p => Math.Round(p.Price, 9)));
        Assert.Equal(0, c.DroppedPoints);
        Assert.Equal(D3, c.LastQuote!.ObservationDate);
    }

    [Fact]
    public void BaseReturn_EqualsLocalReturnTimesFxReturn()
    {
        var native = Series("7203.T", (D1, 3000), (D2, 3150));                 // +5 % in JPY
        var c = CurrencyConverter.Convert(native, "JPY", "USD", Fx);
        var local = 3150.0 / 3000 - 1;
        var fxReturn = Fx.Rate("JPY", "USD", D2).Rate / Fx.Rate("JPY", "USD", D1).Rate - 1;   // USD per JPY: (1.00/160)/(1.10/160) − 1
        var baseReturn = c.Series.Points[1].Price / c.Series.Points[0].Price - 1;
        Assert.Equal((1 + local) * (1 + fxReturn) - 1, baseReturn, 12);
        Assert.NotEqual(local + fxReturn, baseReturn, 6);                        // not the naive sum
    }

    [Fact]
    public void SameCurrency_IsReturnedUnchanged_SoConversionCannotHappenTwice()
    {
        var eur = Series("SAP.DE", (D1, 200), (D2, 210));
        var c = CurrencyConverter.Convert(eur, "EUR", "EUR", null);
        Assert.Same(eur, c.Series);
        // Converting an already converted series again only happens with the wrong currency label – the base currency
        // label of a converted series makes a second call a no-op.
        var once = CurrencyConverter.Convert(Series("AAPL", (D1, 110)), "USD", "EUR", Fx);
        Assert.Same(once.Series, CurrencyConverter.Convert(once.Series, "EUR", "EUR", Fx).Series);
    }

    [Fact]
    public void PointsWithoutRates_AreDroppedAndCounted_NotFilled()
    {
        var c = CurrencyConverter.Convert(Series("AAPL", (D1, 110), (new DateOnly(2026, 11, 2), 120)), "USD", "EUR", Fx);
        Assert.Single(c.Series.Points);
        Assert.Equal(1, c.DroppedPoints);
    }

    [Fact]
    public void ChangingTheBaseCurrency_DoesNotChangeTheNativeSeries()
    {
        var native = Series("AAPL", (D1, 110), (D2, 110));
        var before = native.Points.ToList();
        _ = CurrencyConverter.Convert(native, "USD", "EUR", Fx);
        _ = CurrencyConverter.Convert(native, "USD", "JPY", Fx);
        Assert.Equal(before, native.Points);
    }
}

public sealed class FxProviderTests
{
    [Fact]
    public void Ecb_CsvIsParsedAsUnitsPerEur_AndSkipsBlankObservations()
    {
        // Shape of data-api.ecb.europa.eu csvdata&detail=dataonly (2026-10-15), shortened.
        const string csv = """
            KEY,FREQ,CURRENCY,CURRENCY_DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE
            EXR.D.CHF.EUR.SP00.A,D,CHF,EUR,SP00,A,2026-10-14,0.933
            EXR.D.CHF.EUR.SP00.A,D,CHF,EUR,SP00,A,2026-10-15,0.9305
            EXR.D.JPY.EUR.SP00.A,D,JPY,EUR,SP00,A,2026-10-15,
            EXR.D.USD.EUR.SP00.A,D,USD,EUR,SP00,A,2026-10-15,1.0952
            """;
        var rows = EcbFxProvider.Parse(csv);
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r is { Currency: "USD", UnitsPerEur: 1.0952m } && r.Date == new DateOnly(2026, 10, 15));
    }

    [Fact]
    public void Ecb_RejectsAnUnexpectedQuoteDirection()
    {
        const string csv = "KEY,FREQ,CURRENCY,CURRENCY_DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE\nEXR.D.EUR.USD.SP00.A,D,EUR,USD,SP00,A,2026-10-15,0.91";
        Assert.Throws<PortfolioOptimizer.Application.Abstractions.FxProviderException>(() => EcbFxProvider.Parse(csv));
    }

    [Fact]
    public async Task Mock_IsDeterministic_WeekdaysOnly_AndRealisticallyScaled()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var a = await new MockFxProvider(clock).GetRatesAsync(["USD", "JPY"], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
        var b = await new MockFxProvider(clock).GetRatesAsync(["USD", "JPY"], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
        Assert.Equal(a, b);
        Assert.DoesNotContain(a, r => r.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.All(a.Where(r => r.Currency == "USD"), r => Assert.InRange(r.UnitsPerEur, 0.6m, 2m));
        Assert.All(a.Where(r => r.Currency == "JPY"), r => Assert.InRange(r.UnitsPerEur, 80m, 300m));
    }

    [Fact]
    public async Task Bgn_IsDiscontinued_HistoryOnly_UntilTheEuroChangeover()
    {
        // Bulgaria adopted the euro on 2026-01-01; the ECB's last BGN reference rate is dated 2025-12-31.
        Assert.DoesNotContain("BGN", EcbFxProvider.Currencies);
        Assert.Equal(29, EcbFxProvider.Currencies.Count);
        Assert.Equal(new DateOnly(2025, 12, 31), EcbFxProvider.Discontinued["BGN"]);
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var mock = new MockFxProvider(clock);
        Assert.Equal(EcbFxProvider.Discontinued, mock.DiscontinuedCurrencies);
        var rows = await mock.GetRatesAsync(["BGN"], new DateOnly(2025, 12, 1), new DateOnly(2026, 2, 1), CancellationToken.None);
        Assert.NotEmpty(rows);
        Assert.Equal(new DateOnly(2025, 12, 31), rows.Max(r => r.Date));     // simulated rates end like the real ones
        Assert.Contains("1.95583", PortfolioOptimizer.Application.Abstractions.FxDiscontinued.Reason("BGN", new DateOnly(2025, 12, 31)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Opt-in (PO_LIVE_SMOKE=1): the real ECB data API returns parseable rates in the expected direction.</summary>
public sealed class LiveEcbSmokeTests
{
    [PortfolioOptimizer.UnitTests.MarketData.LiveSmokeFact]
    public async Task Ecb_ReturnsRecentRates_ForUsdGbpJpyChf()
    {
        var provider = new EcbFxProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Microsoft.Extensions.Options.Options.Create(new EcbFxOptions()));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await provider.GetRatesAsync(["USD", "GBP", "JPY", "CHF"], to.AddDays(-14), to, CancellationToken.None);
        foreach (var c in new[] { "USD", "GBP", "JPY", "CHF" })
            Assert.Contains(rows, r => r.Currency == c);
        // Units per EUR: plausibility ranges guard against an inverted direction.
        Assert.All(rows.Where(r => r.Currency == "USD"), r => Assert.InRange(r.UnitsPerEur, 0.7m, 2m));
        Assert.All(rows.Where(r => r.Currency == "JPY"), r => Assert.InRange(r.UnitsPerEur, 80m, 300m));
        Assert.All(rows, r => Assert.True(r.Date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday));
    }

    [PortfolioOptimizer.UnitTests.MarketData.LiveSmokeFact]
    public async Task Ecb_PublishesEveryListedCurrency_AndNoBgnAfter2025()
    {
        var provider = new EcbFxProvider(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, Microsoft.Extensions.Options.Options.Create(new EcbFxOptions()));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await provider.GetRatesAsync(EcbFxProvider.Currencies, to.AddDays(-14), to, CancellationToken.None);
        Assert.Equal(EcbFxProvider.Currencies.Order(), rows.Select(r => r.Currency).Distinct().Order());
        var bgn = await provider.GetRatesAsync(["BGN"], new DateOnly(2025, 12, 1), to, CancellationToken.None);
        Assert.Equal(EcbFxProvider.Discontinued["BGN"], bgn.Max(r => r.Date));
    }
}
