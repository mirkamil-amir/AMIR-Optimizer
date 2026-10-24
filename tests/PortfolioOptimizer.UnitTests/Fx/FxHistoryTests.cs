using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Fx;

/// <summary>
/// Long FX histories (regression for 2026-10-23: an 8-year ECB request timed out, nothing older than five years was ever
/// stored, and every European/Japanese stock with eight years of prices was excluded from USD analyses).
/// </summary>
public sealed class FxDownloadPlanTests
{
    private static readonly DateOnly From = new(2018, 10, 16), To = new(2026, 10, 23);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chunks_CoverTheRangeExactly_WithoutOverlapOrGap(bool backward)
    {
        var chunks = FxDownloadPlan.Chunks(From, To, backward).ToList();
        Assert.All(chunks, c => Assert.True(c.To.DayNumber - c.From.DayNumber + 1 <= FxDownloadPlan.ChunkDays));
        var ordered = chunks.OrderBy(c => c.From).ToList();
        Assert.Equal(From, ordered[0].From);
        Assert.Equal(To, ordered[^1].To);
        for (var i = 1; i < ordered.Count; i++) Assert.Equal(ordered[i - 1].To.AddDays(1), ordered[i].From);
        Assert.Equal(5, chunks.Count);                                   // 2930 days in chunks of 731
        // Backward: newest first, so every stored chunk joins the stored range; forward: oldest first.
        Assert.Equal(backward ? To : ordered[0].To, chunks[0].To);
    }

    [Fact]
    public void Chunks_OfOneDay_AndOfAnEmptyRange()
    {
        Assert.Equal([(To, To)], FxDownloadPlan.Chunks(To, To, true));
        Assert.Equal([(To, To)], FxDownloadPlan.Chunks(To, To, false));
        Assert.Empty(FxDownloadPlan.Chunks(To, From, true));
    }

    private static IEnumerable<FxObservation> Weekdays(string currency, DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) yield return new FxObservation(currency, d, 1.1m);
    }

    [Fact]
    public void Connected_ExtendsTheStoredRangeBackward_AndStopsAtAHole()
    {
        var storedMin = new DateOnly(2021, 10, 8);
        // Provider answer for 2019-10-01 … 2021-10-07 with a missing month in 2020 (a partial answer).
        var answer = Weekdays("USD", new DateOnly(2019, 10, 1), new DateOnly(2021, 10, 7))
            .Where(o => o.Date < new DateOnly(2020, 3, 1) || o.Date > new DateOnly(2020, 3, 31)).ToList();
        var kept = FxDownloadPlan.Connected(answer, storedMin, new DateOnly(2026, 10, 23));
        // Only the part joining the stored range is kept; storing the older part would leave a hidden hole in March 2020.
        Assert.Equal(new DateOnly(2020, 4, 1), kept.Min(o => o.Date));
        Assert.Equal(new DateOnly(2021, 10, 7), kept.Max(o => o.Date));
    }

    [Fact]
    public void Connected_AcceptsPublicationPauses_LikeEaster()
    {
        // Thursday 2025-04-17 → Tuesday 2025-04-22: no ECB rate on Good Friday and Easter Monday.
        var answer = Weekdays("GBP", new DateOnly(2025, 4, 1), new DateOnly(2025, 4, 30))
            .Where(o => o.Date != new DateOnly(2025, 4, 18) && o.Date != new DateOnly(2025, 4, 21)).ToList();
        Assert.Equal(answer.Count, FxDownloadPlan.Connected(answer, null, null).Count);
    }

    [Fact]
    public void Connected_WithoutStoredData_KeepsTheRunEndingAtTheNewestRate_AndForwardStopsAtAHole()
    {
        var answer = Weekdays("JPY", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31))
            .Concat(Weekdays("JPY", new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 29))).ToList();
        var fresh = FxDownloadPlan.Connected(answer, null, null);
        Assert.Equal(new DateOnly(2024, 3, 1), fresh.Min(o => o.Date));
        var forward = FxDownloadPlan.Connected(answer, new DateOnly(2023, 1, 2), new DateOnly(2023, 12, 29));
        Assert.Equal(new DateOnly(2024, 1, 31), forward.Max(o => o.Date));
        Assert.Empty(FxDownloadPlan.Connected(answer, new DateOnly(2022, 1, 3), new DateOnly(2022, 12, 30)));   // too far away to join
    }
}

public sealed class FxMatchingTests
{
    private static readonly DateOnly Thu = new(2026, 10, 1), Mon = new(2026, 10, 5), Tue = new(2026, 10, 6);

    [Fact]
    public void ARateExactlyAtTheMaximumAge_IsUsed_OneDayOlderIsNot()
    {
        var t = new FxTable("TEST", false, [("USD", Thu, 1.10)]);
        var q = t.Rate("EUR", "USD", Mon);                                 // 4 days
        Assert.Equal(4, q.CarriedForwardDays);
        Assert.False(t.TryRate("EUR", "USD", Tue, out _, out var why));    // 5 days
        Assert.Contains("at most 4", why);
    }

    [Fact]
    public void ALaterRate_IsNeverUsedForAnEarlierPrice()
    {
        var t = new FxTable("TEST", false, [("USD", Tue, 1.10)]);
        Assert.False(t.TryRate("EUR", "USD", Mon, out _, out var why));
        Assert.Contains("first stored USD rate", why);
    }

    private static PriceSeries Weekdays(DateOnly from, DateOnly to)
    {
        var points = new List<PricePoint>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) points.Add(new PricePoint(d, 100));
        return new PriceSeries("BAYN.DE", 1, points, true, false);
    }

    private static IEnumerable<(string, DateOnly, double)> Rates(string currency, DateOnly from, DateOnly to, double value)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) yield return (currency, d, value);
    }

    [Fact]
    public void ReportedCase_HistoryBeforeTheFirstStoredRate_IsCountedAsSuch()
    {
        // Eight years of EUR prices, USD rates stored from 2021-10-08 only (the state of the development database).
        var prices = Weekdays(new DateOnly(2018, 10, 23), new DateOnly(2026, 10, 23));
        var fx = new FxTable("TEST", false, Rates("USD", new DateOnly(2021, 10, 8), new DateOnly(2026, 10, 23), 1.1));
        var c = CurrencyConverter.Convert(prices, "EUR", "USD", fx);
        var expected = prices.Points.Count(p => p.Date < new DateOnly(2021, 10, 8));
        Assert.Equal(expected, c.DroppedPoints);
        Assert.Equal(new FxMissing(expected, 0, 0, new DateOnly(2021, 10, 8), new DateOnly(2026, 10, 23)), c.Missing);
        Assert.Contains("before the first stored rate (2021-10-08)", c.Missing!.Describe());

        // With the full history stored, nothing is dropped and the values are price × rate.
        var full = new FxTable("TEST", false, Rates("USD", new DateOnly(2018, 10, 15), new DateOnly(2026, 10, 23), 1.1));
        var all = CurrencyConverter.Convert(prices, "EUR", "USD", full);
        Assert.Equal(0, all.DroppedPoints);
        Assert.Null(all.Missing);
        Assert.All(all.Series.Points, p => Assert.Equal(110.0, p.Price, 9));
    }

    [Fact]
    public void GapsAndTheEnd_AreCountedSeparately()
    {
        var prices = Weekdays(new DateOnly(2026, 1, 5), new DateOnly(2026, 3, 31));
        // GBP and USD legs; USD has a February hole, GBP ends 2026-03-13.
        var fx = new FxTable("TEST", false,
            Rates("USD", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 1.1).Where(r => r.Item2.Month != 2)
                .Concat(Rates("GBP", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 13), 0.85)));
        var c = CurrencyConverter.Convert(prices with { Symbol = "ABF.L" }, "GBP", "USD", fx);
        Assert.Equal(new DateOnly(2026, 3, 13), c.Missing!.LastRate);
        Assert.Equal(prices.Points.Count(p => p.Date > new DateOnly(2026, 3, 17)), c.Missing.AfterLastRate);
        Assert.Equal(prices.Points.Count(p => p.Date.Month == 2 && p.Date.Day > 3), c.Missing.InGaps);   // Feb 2 and 3 carry 2026-01-30 (3 and 4 days)
        Assert.Equal(0, c.Missing.BeforeFirstRate);
        Assert.Equal(c.DroppedPoints, c.Missing.BeforeFirstRate + c.Missing.InGaps + c.Missing.AfterLastRate);
    }

    [Fact]
    public void AnEmptySeries_NeedsNoRates()
    {
        var c = CurrencyConverter.Convert(new PriceSeries("X", 1, [], true, false), "JPY", "USD", null);
        Assert.Empty(c.Series.Points);
        Assert.Equal(0, c.DroppedPoints);
    }
}
