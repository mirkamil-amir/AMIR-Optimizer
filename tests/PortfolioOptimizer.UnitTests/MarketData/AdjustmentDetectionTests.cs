using PortfolioOptimizer.Application.MarketData;

namespace PortfolioOptimizer.UnitTests.MarketData;

/// <summary>When a re-downloaded overlap bar means a new dividend/split adjustment (full history refresh) – hand-built bars.</summary>
public sealed class AdjustmentDetectionTests
{
    [Fact]
    public void PriceMoveWithoutNewAdjustment_IsNotAnAdjustment()
    {
        // Stored while the session was open (100, factor 0.98), re-downloaded after the close (103, same factor).
        Assert.False(MarketDataService.AdjustmentChanged(100m, 98m, 103m, 100.94m));
        // No adjustment at all: adjusted = close before and after.
        Assert.False(MarketDataService.AdjustmentChanged(100m, 100m, 97m, 97m));
    }

    [Fact]
    public void NewDividend_ChangesTheFactor_AndIsAnAdjustment()
    {
        // Same close; a 1 % dividend after this date back-adjusts it: factor 1.00 → 0.99.
        Assert.True(MarketDataService.AdjustmentChanged(50m, 50m, 50m, 49.5m));
        // A 2-for-1 split back-adjusts the stored bar: factor 1.0 → 0.5.
        Assert.True(MarketDataService.AdjustmentChanged(200m, 200m, 200m, 100m));
    }

    [Fact]
    public void FactorChangeWithinTheTolerance_IsRoundingNotAnAdjustment()
    {
        // 0.02 % factor change (adjusted closes are stored with 6 decimals, R-11) stays below the 0.05 % tolerance.
        Assert.False(MarketDataService.AdjustmentChanged(10m, 9.8m, 10m, 9.79804m));
    }

    [Fact]
    public void MissingOrNonPositiveValues_AreNeverAnAdjustment()
    {
        Assert.False(MarketDataService.AdjustmentChanged(100m, null, 100m, 99m));
        Assert.False(MarketDataService.AdjustmentChanged(100m, 99m, 100m, null));
        Assert.False(MarketDataService.AdjustmentChanged(0m, 0m, 100m, 99m));
    }
}
