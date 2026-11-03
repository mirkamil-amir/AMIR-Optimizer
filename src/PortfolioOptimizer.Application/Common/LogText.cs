using System.Text.RegularExpressions;

namespace PortfolioOptimizer.Application.Common;

/// <summary>
/// Makes request-supplied text safe for a log line (log forging, CWE-117): line breaks and other control characters
/// become a visible <c>\uXXXX</c> escape, so a crafted path or message can never start a fake log entry. Long values are
/// cut to <c>max</c> characters. Use it for anything a client controls – paths, query values, exception
/// messages that echo input, identifiers from the request.
/// </summary>
/// <remarks>
/// Every non-empty result comes out of <see cref="Regex.Replace(string, MatchEvaluator)"/>: CodeQL's
/// <c>cs/log-forging</c> query only recognizes <c>String.Replace/Remove</c> or <c>Regex.Replace</c> as sanitizers, so a
/// hand-written escape loop (or a fast path that returns the input unchanged) is reported although it is safe.
/// </remarks>
public static partial class LogText
{
    public const int DefaultMax = 500;

    [GeneratedRegex(@"\p{Cc}")]
    private static partial Regex ControlCharacter();

    public static string Clean(string? value, int max = DefaultMax)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var cut = value.Length > max ? value[..max] : value;
        var clean = ControlCharacter().Replace(cut, m => $"\\u{(int)m.Value[0]:x4}");
        return value.Length > max ? clean + "…" : clean;
    }
}
