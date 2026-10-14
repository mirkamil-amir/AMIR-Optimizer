using PortfolioOptimizer.MachineLearning.Data;
using PortfolioOptimizer.MachineLearning.Features;
using PortfolioOptimizer.MachineLearning.Labels;

namespace PortfolioOptimizer.MachineLearning.Tests;

public class FeatureAndLabelTests
{
    private static (List<InstrumentHistory>, InstrumentHistory) Deterministic(int days, Func<int, double> stockPrice, Func<int, double>? benchPrice = null)
    {
        var dates = SyntheticMarket.BusinessDays(days);
        var stock = new InstrumentHistory("AAA", "Tech", dates.Select((d, t) => new DailyObservation(d, stockPrice(t), 1000 + t)).ToList());
        var peer1 = new InstrumentHistory("BBB", "Tech", dates.Select((d, t) => new DailyObservation(d, 50 * Math.Pow(1.001, t), 2000)).ToList());
        var peer2 = new InstrumentHistory("CCC", "Tech", dates.Select((d, t) => new DailyObservation(d, 70 * Math.Pow(1.0005, t), 3000)).ToList());
        var bench = new InstrumentHistory("BENCH", null, dates.Select((d, t) => new DailyObservation(d, (benchPrice ?? (x => 100 * Math.Pow(1.0002, x)))(t), null)).ToList());
        return ([stock, peer1, peer2], bench);
    }

    private static double F(FeatureEngine engine, string name, int asset, int t) => engine.Compute(asset, t)![engine.Names.ToList().IndexOf(name)];

    [Fact]
    public void Momentum_MovingAverage_AndDrawdown_MatchHandComputation()
    {
        var (assets, bench) = Deterministic(400, t => 100 * Math.Pow(1.01, t));
        var panel = MarketPanel.Build(assets, bench);
        var engine = new FeatureEngine(panel, FeatureCatalog.All);
        const int t = 300;
        Assert.Equal(Math.Pow(1.01, 21) - 1, F(engine, "mom_21", 0, t), 10);
        Assert.Equal(Math.Pow(1.01, 5) - 1, F(engine, "ret_5", 0, t), 10);
        Assert.Equal(Math.Pow(1.01, 252 - 21) - 1, F(engine, "mom_12_1", 0, t), 8);
        var sma50 = Enumerable.Range(t - 49, 50).Average(i => 100 * Math.Pow(1.01, i));
        Assert.Equal(100 * Math.Pow(1.01, t) / sma50 - 1, F(engine, "ma_gap_50", 0, t), 10);
        Assert.Equal(0, F(engine, "drawdown_126", 0, t), 12);       // monotone increase: at the high
        Assert.Equal(0, F(engine, "vol_21", 0, t), 8);              // constant return → zero volatility
        Assert.Equal(0, F(engine, "downside_vol_63", 0, t), 12);
        Assert.Equal(0.01, F(engine, "max_ret_21", 0, t), 10);
        Assert.Equal(Math.Pow(1.01, 63) - Math.Pow(1.0002, 63), F(engine, "excess_mom_63", 0, t), 8);
        // volume = 1000 + t
        Assert.Equal((1000 + Enumerable.Range(t - 20, 21).Average()) / (1000 + Enumerable.Range(t - 62, 63).Average()), F(engine, "rel_volume_21_63", 0, t), 10);
    }

    [Fact]
    public void BetaAndCorrelation_AreOneForAScaledCopyOfTheBenchmark()
    {
        var random = new Random(3);
        var bench = new double[400];
        bench[0] = 100;
        for (var t = 1; t < bench.Length; t++) bench[t] = bench[t - 1] * (1 + 0.01 * (random.NextDouble() - 0.5));
        var (assets, b) = Deterministic(400, t => 3 * bench[t], t => bench[t]);
        var engine = new FeatureEngine(MarketPanel.Build(assets, b), FeatureCatalog.All);
        Assert.Equal(1, F(engine, "beta_126", 0, 350), 8);
        Assert.Equal(1, F(engine, "corr_126", 0, 350), 8);
    }

    [Fact]
    public void SectorRelativeMomentum_UsesSameDatePeerMedian()
    {
        var (assets, bench) = Deterministic(300, t => 10 * Math.Pow(1.002, t));
        var engine = new FeatureEngine(MarketPanel.Build(assets, bench), FeatureCatalog.All);
        var moms = new[] { Math.Pow(1.002, 63) - 1, Math.Pow(1.001, 63) - 1, Math.Pow(1.0005, 63) - 1 };
        Assert.Equal(moms[0] - moms[1], F(engine, "sector_rel_mom_63", 0, 250), 10); // median of the three is BBB's
    }

    [Fact]
    public void Features_AreMissing_UntilTheWindowIsComplete()
    {
        var (assets, bench) = Deterministic(300, t => 100 + t);
        var engine = new FeatureEngine(MarketPanel.Build(assets, bench), FeatureCatalog.All);
        Assert.True(double.IsNaN(F(engine, "mom_12_1", 0, 100)));
        Assert.True(double.IsNaN(F(engine, "ma_gap_200", 0, 150)));
        Assert.False(double.IsNaN(F(engine, "mom_21", 0, 100)));
        Assert.Equal(253, engine.RequiredHistory);
    }

    [Fact]
    public void Features_NeverUseFutureObservations()
    {
        var market = SyntheticMarket.Generate(12, 700, 0.1, seed: 11);
        var panel = MarketPanel.Build(market.Assets, market.Benchmark);
        var cutoffIndex = 500;
        var perturbed = SyntheticMarket.PerturbAfter(market, panel.Dates[cutoffIndex]);
        var panel2 = MarketPanel.Build(perturbed.Assets, perturbed.Benchmark);
        var a = new FeatureEngine(panel, FeatureCatalog.All);
        var b = new FeatureEngine(panel2, FeatureCatalog.All);
        for (var asset = 0; asset < panel.AssetCount; asset++)
        for (var t = 260; t <= cutoffIndex; t += 7)
        {
            var x = a.Compute(asset, t)!;
            var y = b.Compute(asset, t)!;
            for (var k = 0; k < x.Length; k++)
                Assert.True(x[k].Equals(y[k]), $"{a.Names[k]} of asset {asset} at t={t} changed when only later prices changed.");
        }
        // Sanity check: the perturbation does change later features.
        Assert.NotEqual(a.Compute(0, cutoffIndex + 30)![1], b.Compute(0, cutoffIndex + 30)![1]);
    }

    [Fact]
    public void Labels_UseExactlyTheNextHorizon_AndAreMissingBeyondTheData()
    {
        var (assets, bench) = Deterministic(300, t => 100 * Math.Pow(1.01, t), t => 100 * Math.Pow(1.002, t));
        var panel = MarketPanel.Build(assets, bench);
        const int t = 200;
        Assert.Equal(Math.Pow(1.01, 21) - 1, LabelEngine.Compute(panel, 0, t, PredictionTarget.ForwardReturn, 21), 10);
        Assert.Equal(Math.Pow(1.01, 21) - Math.Pow(1.002, 21), LabelEngine.Compute(panel, 0, t, PredictionTarget.ForwardExcessReturn, 21), 10);
        Assert.Equal(1, LabelEngine.Compute(panel, 0, t, PredictionTarget.OutperformBenchmark, 21));
        Assert.Equal(0, LabelEngine.Compute(panel, 0, t, PredictionTarget.ForwardVolatility, 21), 8);
        Assert.True(double.IsNaN(LabelEngine.Compute(panel, 0, 290, PredictionTarget.ForwardReturn, 21)));
        Assert.Equal(1, LabelEngine.Compute(panel, 0, 278, PredictionTarget.OutperformBenchmark, 21)); // t+h = 299, the last index
    }

    [Fact]
    public void Dataset_RecordsLabelAvailability_AndKeepsTheLatestDateUnlabeled()
    {
        var panel = SyntheticMarket.Panel(5, 600, 0, seed: 2);
        var dataset = Dataset.Build(panel, new FeatureEngine(panel, FeatureCatalog.All), PredictionTarget.ForwardExcessReturn, 21, 5);
        Assert.All(dataset.Samples, s => Assert.Equal(s.DecisionIndex + 21, s.LabelAvailableIndex));
        Assert.All(dataset.Samples.Where(s => s.IsLabeled), s => Assert.True(s.LabelAvailableIndex < panel.Length));
        Assert.All(dataset.Samples.Where(s => s.DecisionIndex + 21 >= panel.Length), s => Assert.False(s.IsLabeled));
        Assert.Contains(dataset.Samples, s => s.DecisionIndex == panel.Length - 1);
        Assert.True(dataset.Samples.Min(s => s.DecisionIndex) >= 253);
    }

    [Fact]
    public void Panel_ForwardFillsShortGapsOnly_AndNeverBackfills()
    {
        var dates = SyntheticMarket.BusinessDays(40);
        var stock = new InstrumentHistory("AAA", null, dates.Where((_, i) => i is >= 5 and not 10 and not (>= 20 and <= 29))
            .Select(d => new DailyObservation(d, 10, null)).ToList());
        var bench = new InstrumentHistory("B", null, dates.Select(d => new DailyObservation(d, 100, null)).ToList());
        var panel = MarketPanel.Build([stock], bench, maxForwardFillDays: 5);
        Assert.True(double.IsNaN(panel.Prices[0][4]));   // before the first observation
        Assert.Equal(10, panel.Prices[0][10]);            // 1-day gap filled from the previous close
        Assert.True(double.IsNaN(panel.Prices[0][25]));  // 10-day gap left missing
        Assert.Contains(panel.Notes, n => n.Contains("forward-filled"));
    }
}
