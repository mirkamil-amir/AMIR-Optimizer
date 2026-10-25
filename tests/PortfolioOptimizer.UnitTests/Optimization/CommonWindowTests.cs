using PortfolioOptimizer.Optimization.Statistics;

namespace PortfolioOptimizer.UnitTests.Optimization;

public class CommonWindowTests
{
    private static readonly DateOnly End = new(2026, 10, 23);

    /// <summary>Weekday prices from <paramref name="days"/> business days before <paramref name="end"/> to it.</summary>
    private static IReadOnlyList<PricePoint> Series(int days, DateOnly? end = null)
    {
        var list = new List<PricePoint>();
        var d = end ?? End;
        while (list.Count < days)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) list.Add(new PricePoint(d, 100 + list.Count));
            d = d.AddDays(-1);
        }
        list.Reverse();
        return list;
    }

    [Fact]
    public void RecentListing_IsLeftOut_SoTheOthersKeepTheirWindow()
    {
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["A"] = Series(750), ["B"] = Series(750), ["C"] = Series(750), ["NEW"] = Series(30),
        };
        var (kept, excluded) = CommonWindow.Choose(series, ["A", "B", "C", "NEW"], targetDates: 375);

        Assert.Equal(["A", "B", "C"], kept);
        Assert.Equal("NEW", Assert.Single(excluded).Symbol);
        Assert.Contains("too recent", excluded[0].Reason);
        Assert.Equal(750, CommonWindow.CommonDates(series, kept));
    }

    [Fact]
    public void StaleHistory_IsLeftOut()
    {
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["A"] = Series(500), ["B"] = Series(500), ["OLD"] = Series(500, End.AddDays(-60)),
        };
        var (kept, excluded) = CommonWindow.Choose(series, ["A", "B", "OLD"], targetDates: 250);

        Assert.Equal(["A", "B"], kept);
        Assert.Contains("prices end", Assert.Single(excluded).Reason);
    }

    [Fact]
    public void LeavesOutOnlyAsFewAssetsAsNeeded()
    {
        var series = new Dictionary<string, IReadOnlyList<PricePoint>>
        {
            ["A"] = Series(750), ["B"] = Series(400), ["C"] = Series(200), ["D"] = Series(100),
        };
        var (kept, excluded) = CommonWindow.Choose(series, ["A", "B", "C", "D"], targetDates: 300);

        // B (400 dates) is long enough; only C and D – the two most recent listings – go.
        Assert.Equal(["A", "B"], kept);
        Assert.Equal(["C", "D"], excluded.Select(x => x.Symbol).Order());
    }

    [Fact]
    public void KeepsEverything_WhenTheWindowIsLongEnoughOrCannotBeReached()
    {
        var full = new Dictionary<string, IReadOnlyList<PricePoint>> { ["A"] = Series(400), ["B"] = Series(380) };
        Assert.Empty(CommonWindow.Choose(full, ["A", "B"], targetDates: 300).Excluded);

        var shortAll = new Dictionary<string, IReadOnlyList<PricePoint>> { ["A"] = Series(80), ["B"] = Series(70), ["C"] = Series(60) };
        var (kept, excluded) = CommonWindow.Choose(shortAll, ["A", "B", "C"], targetDates: 300);
        Assert.Equal(["A", "B", "C"], kept);
        Assert.Empty(excluded);
    }
}
