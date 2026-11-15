using System.Globalization;
using System.Text.RegularExpressions;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>One titled section of a narrative (explanation of a decision or comparison), as shown in the control room.</summary>
public sealed record InterpretationSection(string Key, string Title, IReadOnlyList<string> Paragraphs);

/// <summary>
/// Accepts a model-written narrative only if every number and ticker it mentions comes from the computed evidence and it
/// contains no promise or advice language. Anything else is rejected and the deterministic explanation is shown instead.
/// </summary>
public static partial class NarrativeValidator
{
    private static readonly string[] Forbidden =
    [
        "guarantee", "guaranteed", "risk-free", "risk free", "will outperform", "will beat", "sure to", "certain to", "can't lose", "cannot lose",
        "you should buy", "you should sell", "buy now", "sell now", "safe investment", "best portfolio",
    ];

    // Cautions such as "past returns do not guarantee future results" are allowed: these phrases pass only when a negation
    // closely precedes them. Advice phrases ("you should buy") never pass.
    private static readonly string[] Negatable = ["guarantee", "guaranteed", "risk-free", "risk free", "safe investment", "sure to", "certain to"];
    private static readonly Regex Negation = new(@"\b(no|not|never|cannot|can't|nothing|without|nor|neither|isn't|aren't|doesn't|don't|won't|none)\b[^.;:!?,]{0,25}$", RegexOptions.IgnoreCase);

    private static (string Phrase, string Context)? ForbiddenPhrase(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var f in Forbidden)
            for (var i = lower.IndexOf(f, StringComparison.Ordinal); i >= 0; i = lower.IndexOf(f, i + f.Length, StringComparison.Ordinal))
            {
                var negated = Negatable.Contains(f) && Negation.IsMatch(lower[Math.Max(0, i - 60)..i]);
                if (!negated)
                {
                    var start = Math.Max(0, i - 40);
                    return (f, text[start..Math.Min(text.Length, i + f.Length + 20)].Trim());
                }
            }
        return null;
    }

    private static readonly HashSet<string> CommonUpper = new(StringComparer.Ordinal)
    {
        "ETF", "USD", "EUR", "GBP", "JPY", "AI", "ML", "CAC", "DAX", "FTSE", "US", "UK", "EU", "S&P", "SP", "NYSE", "NASDAQ", "XETRA", "ES", "VAR", "VAR95", "CVAR",
        "HHI", "QP", "ADMM", "API", "OK", "I", "A", "MIC", "ISIN", "GDP", "CEO", "IPO", "R&D",
    };

    public static (bool Ok, string? Reason) Validate(IReadOnlyList<InterpretationSection> sections, string evidenceJson, IReadOnlySet<string> allowedTickers,
        IReadOnlySet<string> knownTickers)
    {
        var allowed = Numbers().Matches(evidenceJson).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        foreach (var text in sections.SelectMany(s => s.Paragraphs))
        {
            if (ForbiddenPhrase(text) is { } bad) return (false, $"contains the phrase “{bad.Phrase}” (…{bad.Context}…)");
            if (FieldName().Match(text) is { Success: true } field && evidenceJson.Contains($"\"{field.Value}\"", StringComparison.Ordinal))
                return (false, $"quotes the internal field name “{field.Value}” instead of explaining it");
            foreach (Match m in Numbers().Matches(text))
            {
                var x = double.Parse(m.Value, CultureInfo.InvariantCulture);
                var isInteger = !m.Value.Contains('.');
                if (!allowed.Any(v => Math.Abs(v - x) <= 0.05 || (isInteger && Math.Abs(Math.Round(v) - x) < 1e-9)))
                    return (false, $"the number {m.Value} is not in the computed results");
            }
            foreach (Match t in Tickers().Matches(text))
                if (!CommonUpper.Contains(t.Value) && knownTickers.Contains(t.Value) && !allowedTickers.Contains(t.Value))
                    return (false, $"mentions {t.Value}, which is not part of this portfolio");
        }
        return (true, null);
    }

    // camelCase identifiers such as estimatedVolatilityPct (checked against the evidence's property names).
    [GeneratedRegex(@"\b[a-z]+(?:[A-Z][a-z0-9]*)+\b")]
    private static partial Regex FieldName();

    [GeneratedRegex(@"(?<![A-Za-z0-9.])-?\d+(?:\.\d+)?")] private static partial Regex Numbers();
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{0,5}(?:[.-][A-Z]{1,2})?\b")] private static partial Regex Tickers();
}

