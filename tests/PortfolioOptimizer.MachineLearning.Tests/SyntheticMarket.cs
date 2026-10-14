using PortfolioOptimizer.MachineLearning.Data;

namespace PortfolioOptimizer.MachineLearning.Tests;

/// <summary>Deterministic synthetic markets for ML tests.</summary>
public static class SyntheticMarket
{
    public static readonly DateOnly Origin = new(2015, 1, 5);

    public static DateOnly[] BusinessDays(int count, DateOnly? from = null)
    {
        var dates = new List<DateOnly>(count);
        var d = from ?? Origin;
        while (dates.Count < count)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday) dates.Add(d);
            d = d.AddDays(1);
        }
        return [.. dates];
    }

    /// <summary>
    /// One-factor market. Each stock's daily return = β·market + idiosyncratic noise + κ·(its own 63-day return)/63, i.e.
    /// with κ &gt; 0 recent winners keep drifting up (a planted, learnable momentum effect using only past prices); with
    /// κ = 0 and <paramref name="marketDrift"/> = 0 expected forward returns are identical for every stock and unpredictable
    /// from the past (any apparent skill on return targets would indicate leakage). A positive drift makes high-beta stocks
    /// earn more on average – a legitimate, learnable cross-sectional effect.
    /// </summary>
    public static (List<InstrumentHistory> Assets, InstrumentHistory Benchmark) Generate(int stocks, int days, double momentumStrength, int seed,
        string[]? sectors = null, double marketDrift = 0.0003)
    {
        var random = new Random(seed);
        var dates = BusinessDays(days);
        var market = new double[days];
        var benchmark = new double[days];
        benchmark[0] = 100;
        for (var t = 1; t < days; t++)
        {
            market[t] = marketDrift + 0.009 * Normal(random);
            benchmark[t] = benchmark[t - 1] * (1 + market[t]);
        }

        var assets = new List<InstrumentHistory>();
        for (var s = 0; s < stocks; s++)
        {
            var beta = 0.6 + 0.8 * random.NextDouble();
            var idio = 0.01 + 0.01 * random.NextDouble();
            var p = new double[days];
            p[0] = 20 + 80 * random.NextDouble();
            var obs = new List<DailyObservation>(days) { new(dates[0], p[0], 1_000_000) };
            for (var t = 1; t < days; t++)
            {
                var momentum = t > 63 ? p[t - 1] / p[t - 64] - 1 : 0;
                var r = beta * market[t] + idio * Normal(random) + momentumStrength * momentum / 63;
                p[t] = Math.Max(0.5, p[t - 1] * (1 + r));
                obs.Add(new DailyObservation(dates[t], p[t], 500_000 + 1_000_000 * random.NextDouble()));
            }
            assets.Add(new InstrumentHistory($"S{s:D2}", sectors is null ? $"Sector{s % 4}" : sectors[s % sectors.Length], obs));
        }
        var bench = new InstrumentHistory("BENCH", null, dates.Select((d, t) => new DailyObservation(d, benchmark[t], 10_000_000)).ToList());
        return (assets, bench);
    }

    public static MarketPanel Panel(int stocks, int days, double momentumStrength, int seed, double marketDrift = 0.0003)
    {
        var (assets, bench) = Generate(stocks, days, momentumStrength, seed, marketDrift: marketDrift);
        return MarketPanel.Build(assets, bench);
    }

    /// <summary>Copy of the histories where every price after <paramref name="cutoff"/> is replaced with arbitrary values.</summary>
    public static (List<InstrumentHistory> Assets, InstrumentHistory Benchmark) PerturbAfter(
        (List<InstrumentHistory> Assets, InstrumentHistory Benchmark) market, DateOnly cutoff)
    {
        static InstrumentHistory Perturb(InstrumentHistory h, DateOnly cutoff) => h with
        {
            Observations = h.Observations.Select(o => o.Date > cutoff
                ? o with { Price = o.Price * (1.5 + Math.Sin(o.Date.DayNumber)), Volume = (o.Volume ?? 0) * 3 + 1 }
                : o).ToList(),
        };
        return (market.Assets.Select(a => Perturb(a, cutoff)).ToList(), Perturb(market.Benchmark, cutoff));
    }

    private static double Normal(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
