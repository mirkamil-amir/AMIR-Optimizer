namespace PortfolioOptimizer.Application.Common;

/// <summary>
/// Makes request-supplied text safe for a log line (log forging, CWE-117): line breaks and other control characters
/// become a visible <c>\uXXXX</c> escape, so a crafted path or message can never start a fake log entry. Long values are
/// cut to <paramref name="max"/> characters. Use it for anything a client controls – paths, query values, exception
/// messages that echo input, identifiers from the request.
/// </summary>
public static class LogText
{
    public const int DefaultMax = 500;

    public static string Clean(string? value, int max = DefaultMax)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var cut = value.Length > max ? value[..max] : value;
        var needsEscape = false;
        foreach (var c in cut)
            if (char.IsControl(c)) { needsEscape = true; break; }
        var clean = needsEscape
            ? string.Concat(cut.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()))
            : cut;
        return value.Length > max ? clean + "…" : clean;
    }
}
