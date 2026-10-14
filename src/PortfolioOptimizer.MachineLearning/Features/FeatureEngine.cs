using PortfolioOptimizer.MachineLearning.Data;

namespace PortfolioOptimizer.MachineLearning.Features;

/// <summary>
/// Computes feature vectors from a <see cref="MarketPanel"/>. Every value for decision index t reads only indices ≤ t
/// (verified by leakage tests that perturb all later prices). Results are cached per (asset, t).
/// </summary>
public sealed class FeatureEngine
{
    private const double TradingDays = 252d;
    private const double MinValidFraction = 0.9;

    private readonly MarketPanel _panel;
    private readonly FeatureDefinition[] _features;
    private readonly double[][] _returns; // [asset][t] = P[t]/P[t-1]-1, NaN if either missing
    private readonly double[] _benchReturns;
    private readonly Dictionary<(int Asset, int T), double[]> _cache = new();
    private readonly Dictionary<int, double[]> _mom63ByDate = new();

    public FeatureEngine(MarketPanel panel, IReadOnlyList<FeatureDefinition> features)
    {
        if (features.Count == 0) throw new ArgumentException("At least one feature is required.");
        _panel = panel;
        _features = [.. features];
        _returns = panel.Prices.Select(DailyReturns).ToArray();
        _benchReturns = DailyReturns(panel.Benchmark);
        RequiredHistory = FeatureCatalog.RequiredHistory(_features);
    }

    public IReadOnlyList<FeatureDefinition> Features => _features;
    public IReadOnlyList<string> Names => _features.Select(f => f.Name).ToArray();

    /// <summary>First decision index at which all windows can be complete.</summary>
    public int RequiredHistory { get; }

    /// <summary>Feature vector of an asset at decision index t (NaN for unavailable features). The asset must have a price at t.</summary>
    public double[]? Compute(int asset, int t)
    {
        if (t < 0 || t >= _panel.Length || double.IsNaN(_panel.Prices[asset][t])) return null;
        if (_cache.TryGetValue((asset, t), out var cached)) return cached;
        var values = new double[_features.Length];
        for (var k = 0; k < _features.Length; k++) values[k] = Value(_features[k].Name, asset, t);
        _cache[(asset, t)] = values;
        return values;
    }

    /// <summary>Annualized 63-day volatility (used for risk-scaled portfolio weights); NaN if unavailable.</summary>
    public double TrailingVolatility(int asset, int t) => Volatility(_returns[asset], t, 63);

    private double Value(string name, int a, int t)
    {
        var p = _panel.Prices[a];
        var r = _returns[a];
        return name switch
        {
            "ret_5" => Change(p, t, 5),
            "mom_21" => Change(p, t, 21),
            "mom_63" => Change(p, t, 63),
            "mom_126" => Change(p, t, 126),
            "mom_12_1" => t - 252 >= 0 && Valid(p[t - 21]) && Valid(p[t - 252]) ? p[t - 21] / p[t - 252] - 1 : double.NaN,
            "ma_gap_50" => MaGap(p, t, 50),
            "ma_gap_200" => MaGap(p, t, 200),
            "vol_21" => Volatility(r, t, 21),
            "vol_63" => Volatility(r, t, 63),
            "downside_vol_63" => DownsideVolatility(r, t, 63),
            "drawdown_126" => Drawdown(p, t, 126),
            "max_ret_21" => MaxReturn(r, t, 21),
            "beta_126" => BetaOrCorrelation(r, t, 126, beta: true),
            "corr_126" => BetaOrCorrelation(r, t, 126, beta: false),
            "excess_mom_63" => Change(p, t, 63) - Change(_panel.Benchmark, t, 63),
            "rel_volume_21_63" => RelativeVolume(a, t),
            "log_dollar_volume_21" => LogDollarVolume(a, t),
            "sector_rel_mom_63" => SectorRelativeMomentum(a, t),
            "market_trend_200" => MaGap(_panel.Benchmark, t, 200),
            "market_vol_63" => Volatility(_benchReturns, t, 63),
            _ => throw new ArgumentException($"Unknown feature '{name}'."),
        };
    }

    private static bool Valid(double x) => !double.IsNaN(x) && x > 0;

    private static double Change(double[] p, int t, int k) =>
        t - k >= 0 && Valid(p[t]) && Valid(p[t - k]) ? p[t] / p[t - k] - 1 : double.NaN;

    private static double MaGap(double[] p, int t, int window)
    {
        if (t - window + 1 < 0 || !Valid(p[t])) return double.NaN;
        var sum = 0d;
        for (var i = t - window + 1; i <= t; i++)
        {
            if (!Valid(p[i])) return double.NaN;
            sum += p[i];
        }
        return p[t] / (sum / window) - 1;
    }

    private static double Volatility(double[] r, int t, int window)
    {
        if (t - window + 1 < 1) return double.NaN;
        double sum = 0, sumSq = 0;
        var n = 0;
        for (var i = t - window + 1; i <= t; i++)
        {
            if (double.IsNaN(r[i])) continue;
            sum += r[i];
            sumSq += r[i] * r[i];
            n++;
        }
        if (n < Math.Max(2, MinValidFraction * window)) return double.NaN;
        var mean = sum / n;
        var variance = (sumSq - n * mean * mean) / (n - 1);
        return Math.Sqrt(Math.Max(variance, 0) * TradingDays);
    }

    private static double DownsideVolatility(double[] r, int t, int window)
    {
        if (t - window + 1 < 1) return double.NaN;
        double sumSq = 0;
        var n = 0;
        for (var i = t - window + 1; i <= t; i++)
        {
            if (double.IsNaN(r[i])) continue;
            var d = Math.Min(0, r[i]);
            sumSq += d * d;
            n++;
        }
        return n < MinValidFraction * window ? double.NaN : Math.Sqrt(sumSq / n * TradingDays);
    }

    private static double Drawdown(double[] p, int t, int window)
    {
        if (t - window + 1 < 0 || !Valid(p[t])) return double.NaN;
        var max = 0d;
        for (var i = t - window + 1; i <= t; i++)
        {
            if (!Valid(p[i])) return double.NaN;
            max = Math.Max(max, p[i]);
        }
        return p[t] / max - 1;
    }

    private static double MaxReturn(double[] r, int t, int window)
    {
        if (t - window + 1 < 1) return double.NaN;
        var max = double.NegativeInfinity;
        var n = 0;
        for (var i = t - window + 1; i <= t; i++)
        {
            if (double.IsNaN(r[i])) continue;
            max = Math.Max(max, r[i]);
            n++;
        }
        return n < MinValidFraction * window ? double.NaN : max;
    }

    private double BetaOrCorrelation(double[] r, int t, int window, bool beta)
    {
        if (t - window + 1 < 1) return double.NaN;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        var n = 0;
        for (var i = t - window + 1; i <= t; i++)
        {
            var x = r[i];
            var y = _benchReturns[i];
            if (double.IsNaN(x) || double.IsNaN(y)) continue;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            n++;
        }
        if (n < MinValidFraction * window) return double.NaN;
        var cov = (sxy - sx * sy / n) / (n - 1);
        var varY = (syy - sy * sy / n) / (n - 1);
        var varX = (sxx - sx * sx / n) / (n - 1);
        if (varY <= 1e-16) return double.NaN;
        if (beta) return cov / varY;
        return varX <= 1e-16 ? double.NaN : cov / Math.Sqrt(varX * varY);
    }

    private double RelativeVolume(int a, int t)
    {
        var shortMean = MeanVolume(a, t, 21);
        var longMean = MeanVolume(a, t, 63);
        return double.IsNaN(shortMean) || double.IsNaN(longMean) || longMean <= 0 ? double.NaN : shortMean / longMean;
    }

    private double LogDollarVolume(int a, int t)
    {
        if (t - 20 < 0) return double.NaN;
        double sum = 0;
        var n = 0;
        for (var i = t - 20; i <= t; i++)
        {
            var v = _panel.Volumes[a][i];
            var p = _panel.Prices[a][i];
            if (double.IsNaN(v) || !Valid(p)) continue;
            sum += v * p;
            n++;
        }
        return n < MinValidFraction * 21 || sum <= 0 ? double.NaN : Math.Log(sum / n);
    }

    private double MeanVolume(int a, int t, int window)
    {
        if (t - window + 1 < 0) return double.NaN;
        double sum = 0;
        var n = 0;
        for (var i = t - window + 1; i <= t; i++)
        {
            var v = _panel.Volumes[a][i];
            if (double.IsNaN(v)) continue;
            sum += v;
            n++;
        }
        return n < MinValidFraction * window ? double.NaN : sum / n;
    }

    /// <summary>63-day return minus the median 63-day return of same-sector universe members on the same date (≥ 3 values).</summary>
    private double SectorRelativeMomentum(int a, int t)
    {
        var sector = _panel.Sectors[a];
        if (sector is null) return double.NaN;
        if (!_mom63ByDate.TryGetValue(t, out var moms))
        {
            moms = Enumerable.Range(0, _panel.AssetCount).Select(i => Change(_panel.Prices[i], t, 63)).ToArray();
            _mom63ByDate[t] = moms;
        }
        if (double.IsNaN(moms[a])) return double.NaN;
        var peers = Enumerable.Range(0, _panel.AssetCount)
            .Where(i => _panel.Sectors[i] == sector && !double.IsNaN(moms[i]))
            .Select(i => moms[i]).OrderBy(x => x).ToArray();
        if (peers.Length < 3) return double.NaN;
        var median = peers.Length % 2 == 1 ? peers[peers.Length / 2] : (peers[peers.Length / 2 - 1] + peers[peers.Length / 2]) / 2;
        return moms[a] - median;
    }

    private static double[] DailyReturns(double[] p)
    {
        var r = new double[p.Length];
        r[0] = double.NaN;
        for (var t = 1; t < p.Length; t++) r[t] = Valid(p[t]) && Valid(p[t - 1]) ? p[t] / p[t - 1] - 1 : double.NaN;
        return r;
    }
}
