namespace PortfolioOptimizer.MachineLearning.Features;

public enum FeatureGroup { PriceMomentum, VolatilityAndDownside, RelativePerformance, TradingActivity, SectorContext, MarketContext }

/// <summary>
/// Documentation of one feature. Every feature is computed at the close of a decision date t using only observations
/// dated ≤ t (adjusted prices, volumes, the benchmark and same-date values of other universe members).
/// </summary>
public sealed record FeatureDefinition(
    string Name,
    FeatureGroup Group,
    string Definition,
    int WindowDays,
    bool RequiresVolume,
    bool RequiresBenchmark,
    bool RequiresSectorPeers);

public static class FeatureCatalog
{
    /// <summary>Version of the feature definitions; recorded with every experiment and model.</summary>
    public const string Version = "features-2026.10-v1";

    public const string MissingValuePolicy =
        "A feature is missing when its window has a missing price or too few valid returns (< 90%). Samples with more than 30% " +
        "missing features are dropped; remaining missing values are imputed with the training-set median of that feature " +
        "(fitted on training data only). No value is ever filled from the future.";

    public static readonly IReadOnlyList<FeatureDefinition> All =
    [
        new("ret_5", FeatureGroup.PriceMomentum, "5-day return P[t]/P[t−5] − 1 (short-term reversal signal).", 5, false, false, false),
        new("mom_21", FeatureGroup.PriceMomentum, "21-day (≈1 month) return P[t]/P[t−21] − 1.", 21, false, false, false),
        new("mom_63", FeatureGroup.PriceMomentum, "63-day (≈3 months) return.", 63, false, false, false),
        new("mom_126", FeatureGroup.PriceMomentum, "126-day (≈6 months) return.", 126, false, false, false),
        new("mom_12_1", FeatureGroup.PriceMomentum, "12-1 momentum: P[t−21]/P[t−252] − 1 (skips the most recent month).", 252, false, false, false),
        new("ma_gap_50", FeatureGroup.PriceMomentum, "Distance to the 50-day simple moving average: P[t]/SMA50 − 1.", 50, false, false, false),
        new("ma_gap_200", FeatureGroup.PriceMomentum, "Distance to the 200-day simple moving average: P[t]/SMA200 − 1 (trend).", 200, false, false, false),
        new("vol_21", FeatureGroup.VolatilityAndDownside, "Annualized standard deviation of daily returns over 21 days (√252 scaling).", 21, false, false, false),
        new("vol_63", FeatureGroup.VolatilityAndDownside, "Annualized standard deviation of daily returns over 63 days.", 63, false, false, false),
        new("downside_vol_63", FeatureGroup.VolatilityAndDownside, "Annualized downside deviation √(252·mean(min(r,0)²)) over 63 days.", 63, false, false, false),
        new("drawdown_126", FeatureGroup.VolatilityAndDownside, "Drawdown from the 126-day high: P[t]/max(P[t−125..t]) − 1.", 126, false, false, false),
        new("max_ret_21", FeatureGroup.VolatilityAndDownside, "Largest single-day return in the last 21 days (extreme-return measure).", 21, false, false, false),
        new("beta_126", FeatureGroup.RelativePerformance, "Beta to the benchmark: cov(r, r_b)/var(r_b) of daily returns over 126 days.", 126, false, true, false),
        new("corr_126", FeatureGroup.RelativePerformance, "Correlation of daily returns with the benchmark over 126 days.", 126, false, true, false),
        new("excess_mom_63", FeatureGroup.RelativePerformance, "63-day return minus the benchmark's 63-day return.", 63, false, true, false),
        new("rel_volume_21_63", FeatureGroup.TradingActivity, "Relative volume: mean volume over 21 days / mean volume over 63 days.", 63, true, false, false),
        new("log_dollar_volume_21", FeatureGroup.TradingActivity, "Liquidity proxy: ln(mean(P·V) over 21 days) with adjusted price P and volume V.", 21, true, false, false),
        new("sector_rel_mom_63", FeatureGroup.SectorContext, "63-day return minus the median 63-day return of the stock's sector peers in the universe (≥ 3 peers incl. itself).", 63, false, false, true),
        new("market_trend_200", FeatureGroup.MarketContext, "Benchmark distance to its 200-day moving average (same value for all stocks on a date).", 200, false, true, false),
        new("market_vol_63", FeatureGroup.MarketContext, "Benchmark annualized volatility over 63 days (same value for all stocks on a date).", 63, false, true, false),
    ];

    public static FeatureDefinition Get(string name) =>
        All.FirstOrDefault(f => f.Name == name) ?? throw new ArgumentException($"Unknown feature '{name}'.");

    /// <summary>Longest window of the given features (+1 for the return difference) – the first usable decision index.</summary>
    public static int RequiredHistory(IEnumerable<FeatureDefinition> features) => features.Max(f => f.WindowDays) + 1;
}
