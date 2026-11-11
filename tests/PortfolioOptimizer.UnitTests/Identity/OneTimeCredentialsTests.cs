using PortfolioOptimizer.Infrastructure.Identity;

namespace PortfolioOptimizer.UnitTests.Identity;

public sealed class OneTimeCredentialsTests
{
    [Fact]
    public void Generate_MeetsThePasswordPolicy_AvoidsLookAlikes_AndDoesNotRepeat()
    {
        var values = Enumerable.Range(0, 500).Select(_ => OneTimeCredentials.Generate()).ToList();
        foreach (var v in values)
        {
            Assert.Matches("^[A-Za-z2-9]{4}(-[A-Za-z2-9]{4}){3}$", v);
            Assert.Contains(v, char.IsUpper);
            Assert.Contains(v, char.IsLower);
            Assert.Contains(v, char.IsDigit);
            Assert.DoesNotContain(v, c => "0O1lI".Contains(c));
        }
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
