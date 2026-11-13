using System.Text.Json;
using System.Text.RegularExpressions;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.Llm;

/// <summary>
/// Deterministic stand-in for the model in Matrix Pilot's automated tests and the e2e stack (provider "Mock", shown as a
/// test model everywhere). It is NOT an AI and makes no investment judgement: it reads the platform's own screening result
/// and returns equal weights within the mandate, or holds when the portfolio already has positions. Production never uses it.
/// </summary>
public static partial class MockPilotScript
{
    public static JsonElement? Respond(LlmRequest request)
    {
        if (!request.Purpose.StartsWith("pilot.", StringComparison.Ordinal)) return null;
        var data = Data(request.UserContent);
        object output = request.Purpose switch
        {
            "pilot.research" => Research(data),
            "pilot.decide" or "pilot.finalize" => Decide(data),
            "pilot.critique" => new
            {
                concerns = new[] { new { area = "evidence", severity = "low", detail = "Test model: deterministic script, no real review." } },
                recommendation = "keep",
                summary = "Test model review (no judgement).",
            },
            _ => new { sections = Array.Empty<object>() }, // the platform's own summary is used
        };
        return JsonSerializer.SerializeToElement(output);
    }

    private static object Research(JsonElement? data)
    {
        var round = data?.TryGetProperty("round", out var r) == true ? r.GetInt32() : 1;
        if (round > 1) return new { notes = "Test model: evidence gathered.", toolCalls = Array.Empty<object>(), ready = true };
        return new
        {
            notes = "Test model: reading the portfolio, mandate and a screen of the universe.",
            toolCalls = new object[]
            {
                new { tool = "get_portfolio_state", argsJson = "{}" },
                new { tool = "get_mandate", argsJson = "{}" },
                new { tool = "screen_universe", argsJson = """{"sortBy":"return_per_volatility","limit":20}""" },
            },
            ready = false,
        };
    }

    private static object Decide(JsonElement? data)
    {
        var holdings = data?.GetProperty("portfolio").GetProperty("holdings").GetArrayLength() ?? 0;
        if (holdings > 0)
            return Decision("HOLD", "hold", [], 0, ["Test model: the portfolio already holds positions; the script holds."]);

        var mandate = data!.Value.GetProperty("mandate");
        var minHoldings = mandate.GetProperty("minHoldings").GetInt32();
        var maxHoldings = mandate.GetProperty("maxHoldings").GetInt32();
        var maxPos = mandate.GetProperty("maxPositionWeight").GetDouble();
        var minCash = mandate.GetProperty("minCashWeight").GetDouble();
        var symbols = ScreenedSymbols(data.Value).ToList();
        var n = Math.Min(maxHoldings, Math.Max(minHoldings, Math.Min(symbols.Count, (int)Math.Ceiling((1 - minCash - 0.01) / maxPos))));
        n = Math.Min(n, symbols.Count);
        if (n == 0) return Decision("HOLD", "hold", [], 0, ["Test model: no screened symbol available."]);
        var w = Math.Floor(Math.Min(maxPos, (1 - minCash - 0.01) / n) * 10_000) / 10_000;
        var targets = symbols.Take(n).Select(s => (s, w)).ToList();
        return Decision("REBALANCE", "equal_weight", targets, Math.Round(1 - w * n, 6), ["Test model: equal weights across the top screened symbols."]);
    }

    private static object Decision(string action, string approach, List<(string Symbol, double Weight)> targets, double cash, string[] rationale) => new
    {
        action,
        strategy = new { approach, components = new[] { "screen_universe" }, reason = "Deterministic test script." },
        targets = targets.Select(t => new
        {
            symbol = t.Symbol, weight = t.Weight, role = "test position", thesis = "Test model: chosen by rank in the screen.",
            whyNow = "Scheduled test cycle.", risks = "None assessed (test model).", invalidation = "Not assessed (test model).",
        }),
        cashWeight = cash,
        rejected = Array.Empty<object>(),
        rationale,
        keyRisks = new[] { "Test model: no risk judgement." },
        invalidationConditions = new[] { "Not assessed (test model)." },
        expectedHoldingDays = 30,
        confidence = "low",
        evidenceSteps = Array.Empty<int>(),
        mandateRequests = Array.Empty<string>(),
        critiqueResponse = "",
    };

    /// <summary>Symbols from the platform's screen_universe result in the research log, in rank order.</summary>
    private static IEnumerable<string> ScreenedSymbols(JsonElement data)
    {
        if (!data.TryGetProperty("research", out var log)) yield break;
        foreach (var entry in log.EnumerateArray())
        {
            if (!entry.TryGetProperty("tool", out var t) || t.GetString() != "screen_universe" || !entry.TryGetProperty("result", out var r)) continue;
            using var doc = JsonDocument.Parse(r.GetString()!);
            if (!doc.RootElement.TryGetProperty("top", out var top)) continue;
            foreach (var row in top.EnumerateArray()) yield return row.GetProperty("symbol").GetString()!;
            yield break;
        }
    }

    private static JsonElement? Data(string content)
    {
        var m = DataBlock().Match(content);
        if (!m.Success) return null;
        try { return JsonDocument.Parse(m.Groups[1].Value).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"<data>(.*)</data>", RegexOptions.Singleline)]
    private static partial Regex DataBlock();
}
