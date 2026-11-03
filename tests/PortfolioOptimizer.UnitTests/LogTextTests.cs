using PortfolioOptimizer.Application.Common;

namespace PortfolioOptimizer.UnitTests;

/// <summary>Request-supplied text in log lines (log forging, CWE-117): control characters are escaped, long values cut.</summary>
public sealed class LogTextTests
{
    [Fact]
    public void LineBreaks_CannotStartAFakeLogEntry()
    {
        var forged = "/api/v1/stocks\r\ninfo: Audit auth.login by admin: True";
        var clean = LogText.Clean(forged);
        Assert.DoesNotContain('\r', clean);
        Assert.DoesNotContain('\n', clean);
        Assert.Equal("/api/v1/stocks\\u000d\\u000ainfo: Audit auth.login by admin: True", clean);
    }

    [Fact]
    public void OrdinaryText_IsUnchanged_AndNullBecomesEmpty()
    {
        Assert.Equal("/api/v1/stocks/SAP.DE/quote", LogText.Clean("/api/v1/stocks/SAP.DE/quote"));
        Assert.Equal("Ünïcode – fine", LogText.Clean("Ünïcode – fine"));
        Assert.Equal("", LogText.Clean(null));
    }

    [Fact]
    public void LongValues_AreCut_WithAVisibleMark()
    {
        var clean = LogText.Clean(new string('a', 600), max: 100);
        Assert.Equal(new string('a', 100) + "…", clean);
    }
}
