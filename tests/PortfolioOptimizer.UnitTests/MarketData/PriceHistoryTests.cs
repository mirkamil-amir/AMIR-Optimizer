using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Watchlists;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>Range slicing and period change on hand-built daily series (expected values computed by hand).</summary>
public sealed class PriceHistoryTests
{
    private static HistoryBar Bar(string date, decimal close, decimal? adjusted = null) => new(DateOnly.Parse(date), close, adjusted);

    [Fact]
    public void OneMonth_UsesLastObservationOnOrBeforeTheStart_AsBase()
    {
        // End 2026-03-16 (Mon) → start 2026-02-16; 2026-02-14/15 is a weekend, so the base is Fri 2026-02-13.
        var bars = new[]
        {
            Bar("2026-02-12", 90m, 90m), Bar("2026-02-13", 100m, 100m), Bar("2026-02-17", 104m, 104m),
            Bar("2026-03-13", 108m, 108m), Bar("2026-03-16", 110m, 110m),
        };
        var slice = PriceHistory.Slice(bars, PriceRange.OneMonth);
        Assert.Equal(DateOnly.Parse("2026-02-13"), slice.BaseDate);
        Assert.Equal(100m, slice.BasePrice);
        Assert.Equal(110m, slice.EndPrice);
        Assert.Equal(0.10, slice.Change!.Value, 12);
        Assert.Equal(4, slice.Points.Count); // base + three later bars; 2026-02-12 is outside the range
        Assert.True(slice.UsesAdjustedClose);
    }

    [Fact]
    public void AdjustedCloses_AreUsed_SoASplitIsNotALoss()
    {
        // 2-for-1 split between the bars: raw close halves, adjusted close (back-adjusted) is continuous.
        var bars = new[] { Bar("2026-01-02", 200m, 100m), Bar("2026-02-02", 105m, 105m) };
        var slice = PriceHistory.Slice(bars, PriceRange.OneMonth);
        Assert.Equal(0.05, slice.Change!.Value, 12);
        Assert.Null(slice.Limitation);
    }

    [Fact]
    public void MissingAdjustedClose_FallsBackToRawCloses_ForTheWholeSlice_AndSaysSo()
    {
        var bars = new[] { Bar("2026-01-02", 100m, 99m), Bar("2026-02-02", 120m) };
        var slice = PriceHistory.Slice(bars, PriceRange.OneMonth);
        Assert.False(slice.UsesAdjustedClose);
        Assert.Equal(0.20, slice.Change!.Value, 12); // raw 120/100, never adjusted 99 mixed with raw 120
        Assert.Contains("Raw closes", slice.Limitation);
    }

    [Fact]
    public void HistoryShorterThanTheRange_GivesNoChange_AndTheAvailableStart()
    {
        var bars = new[] { Bar("2026-01-05", 100m, 100m), Bar("2026-03-02", 130m, 130m) };
        var slice = PriceHistory.Slice(bars, PriceRange.OneYear);
        Assert.Null(slice.Change);
        Assert.Empty(slice.Points);
        Assert.Equal(DateOnly.Parse("2026-01-05"), slice.AvailableFrom);
        Assert.Contains("starts on 2026-01-05", slice.Limitation);
    }

    [Fact]
    public void OneDay_IsUnsupported_BecauseNoIntradayBarsAreStored()
    {
        var slice = PriceHistory.Slice([Bar("2026-03-02", 130m, 130m)], PriceRange.OneDay);
        Assert.False(slice.Supported);
        Assert.Null(slice.Change);
        Assert.Contains("Intraday", slice.Limitation);
    }

    [Fact]
    public void NoBars_AndNonPositivePrices_NeverProduceAChange()
    {
        Assert.Null(PriceHistory.Slice([], PriceRange.OneMonth).Change);
        // A zero close is invalid data and is ignored, so the base falls back to the earlier valid bar.
        var slice = PriceHistory.Slice([Bar("2026-01-02", 50m, 50m), Bar("2026-01-05", 0m, 0m), Bar("2026-02-05", 60m, 60m)], PriceRange.OneMonth);
        Assert.Equal(DateOnly.Parse("2026-01-02"), slice.BaseDate);
        Assert.Equal(0.20, slice.Change!.Value, 12);
    }

    [Fact]
    public void Sparkline_KeepsFirstAndLastObservation()
    {
        var points = Enumerable.Range(0, 250).Select(i => (DateOnly.FromDayNumber(739000 + i), (decimal)i)).ToList();
        var spark = WatchlistService.Downsample(points, 60);
        Assert.Equal(60, spark.Count);
        Assert.Equal(0m, spark[0].Price);
        Assert.Equal(249m, spark[^1].Price);
    }

    /// <summary>Weekday bars from <paramref name="from"/> to <paramref name="to"/>, as a fresh download would store them.</summary>
    private static List<HistoryBar> Weekdays(DateOnly from, DateOnly to)
    {
        var bars = new List<HistoryBar>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) bars.Add(new HistoryBar(d, 100m, 100m));
        return bars;
    }

    [Fact]
    public void FreshDefaultDownload_AlwaysHasA5YearChange()
    {
        // Downloaded on Tue 2026-11-03; the last stored bar is Mon 2026-11-02 (Borsa İstanbul, 2026-11-03).
        var today = new DateOnly(2026, 11, 3);
        var yesterday = new DateOnly(2026, 11, 2);

        // The old start (exactly five years before today) stores 2021-11-03 onward, but 5Y needs 2021-11-02 or earlier.
        var exact = PriceHistory.Slice(Weekdays(today.AddYears(-5), yesterday), PriceRange.FiveYears);
        Assert.Null(exact.Change);
        Assert.Contains("needs a price on or before 2021-11-02", exact.Limitation);

        // The default start adds a month, so the base exists – also with a long holiday right before the base date.
        Assert.Equal(new DateOnly(2021, 10, 3), PriceHistory.DefaultDownloadStart(today));
        var bars = Weekdays(PriceHistory.DefaultDownloadStart(today), yesterday)
            .Where(b => b.Date < new DateOnly(2021, 10, 20) || b.Date > new DateOnly(2021, 11, 2)).ToList(); // two weeks closed
        var slice = PriceHistory.Slice(bars, PriceRange.FiveYears);
        Assert.Equal(0.0, slice.Change!.Value, 12);
        Assert.Equal(new DateOnly(2021, 10, 19), slice.BaseDate);
        Assert.Equal(new DateOnly(2020, 10, 3), PriceHistory.DefaultDownloadStart(today, years: 6));
    }
}
