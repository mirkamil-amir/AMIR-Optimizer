using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.UnitTests.News;

/// <summary>Story grouping, attention points and paging cursor (StoryRules, version 1).</summary>
public sealed class StoryRulesTests
{
    private static readonly DateTime T = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static StoryCandidate Story(string headline, NewsScope scope = NewsScope.Company, NewsEventType type = NewsEventType.Results, string? region = "US",
        int[]? assets = null, string? key = null, DateTime? at = null) =>
        new(Guid.NewGuid(), headline, scope, type, region, at ?? T, at ?? T, (assets ?? [1]).ToHashSet(), new HashSet<string> { key ?? headline });

    private static StoryArticleFacts Article(string headline, NewsScope scope = NewsScope.Company, NewsEventType type = NewsEventType.Results, string? region = "US",
        int[]? assets = null, string? key = null, DateTime? at = null) =>
        new(headline, key ?? headline, scope, type, region, at ?? T.AddHours(1), (assets ?? [1]).ToHashSet());

    [Fact]
    public void Words_DropShortAndStopWords_OverlapIsJaccard()
    {
        Assert.Equal(new HashSet<string> { "apple", "beats", "estimates", "iphone", "sales" }, StoryRules.Words("Apple beats estimates as iPhone sales rise in Q3").Where(w => w != "rise").ToHashSet());
        Assert.Equal(0.5, StoryRules.Overlap(new HashSet<string> { "a1", "b1", "c1" }, new HashSet<string> { "b1", "c1", "d1" }));
        Assert.Equal(0, StoryRules.Overlap(new HashSet<string>(), new HashSet<string> { "x" }));
    }

    [Fact]
    public void SyndicatedCopy_JoinsByHeadlineWords_EvenAcrossEventTypes()
    {
        var story = Story("Apple beats estimates", key: "k1");
        Assert.True(StoryRules.Belongs(story, Article("Apple Beats Estimates!", type: NewsEventType.General, assets: [], key: "k1")));
    }

    [Fact]
    public void SameSubjectTypeAndMostWords_Joins_OtherwiseNot()
    {
        var story = Story("Apple quarterly earnings beat estimates on iPhone demand");
        Assert.True(StoryRules.Belongs(story, Article("Apple earnings beat estimates on strong iPhone demand")));
        Assert.False(StoryRules.Belongs(story, Article("Apple earnings beat estimates on strong iPhone demand", assets: [2])));          // other company
        Assert.False(StoryRules.Belongs(story, Article("Apple earnings beat estimates on strong iPhone demand", type: NewsEventType.Guidance)));
        Assert.False(StoryRules.Belongs(story, Article("Apple opens a new store in Berlin")));                                          // few shared words
        Assert.False(StoryRules.Belongs(story, Article("Apple earnings beat estimates on strong iPhone demand", at: T.AddHours(37))));   // outside 36 h
    }

    [Fact]
    public void ReportsWithoutListings_NeedTheSameRegion()
    {
        var ecb = Story("ECB decides on key interest rates", NewsScope.Macro, NewsEventType.RateDecision, "EU", []);
        Assert.True(StoryRules.Belongs(ecb, Article("ECB decides on key interest rates, deposit rate unchanged", NewsScope.Macro, NewsEventType.RateDecision, "EU", [])));
        Assert.False(StoryRules.Belongs(ecb, Article("ECB decides on key interest rates, deposit rate unchanged", NewsScope.Macro, NewsEventType.RateDecision, "GB", [])));
    }

    [Fact]
    public void Choose_PrefersTheSyndicatedCopy_ThenTheClosestHeadline()
    {
        var copy = Story("Apple earnings beat estimates", key: "same");
        var near = Story("Apple earnings beat estimates on demand");
        var article = Article("Apple earnings beat estimates", key: "same");
        Assert.Equal(copy.Id, StoryRules.Choose([near, copy], article)!.Id);
        Assert.Null(StoryRules.Choose([near], Article("Banks rally on rates", NewsScope.Market, NewsEventType.MarketNews, "EU", [])));
    }

    [Theory]
    [InlineData(NewsEventType.Results, NewsCategory.General, false, 1, 2)]
    [InlineData(NewsEventType.Results, NewsCategory.General, true, 3, 4)]
    [InlineData(NewsEventType.General, NewsCategory.Earnings, false, 1, 2)]   // classified before event types: material category
    [InlineData(NewsEventType.General, NewsCategory.General, false, 2, 1)]
    [InlineData(NewsEventType.MarketNews, NewsCategory.General, false, 1, 0)]
    [InlineData(NewsEventType.OtherRelease, NewsCategory.Macroeconomic, true, 1, 1)]
    public void BasePoints(NewsEventType type, NewsCategory category, bool official, int publishers, int expected) =>
        Assert.Equal(expected, StoryRules.BasePoints(type, category, official, publishers));

    [Fact]
    public void BroadMarketStories_GetOnePoint_OnlyWithTwoOrMoreInstruments()
    {
        Assert.Equal(0, StoryRules.BasePoints(NewsEventType.MarketNews, NewsCategory.General, false, 1, marketInstruments: 1));
        Assert.Equal(1, StoryRules.BasePoints(NewsEventType.MarketNews, NewsCategory.General, false, 1, marketInstruments: 2));
        Assert.Equal(2, StoryRules.BasePoints(NewsEventType.Results, NewsCategory.General, false, 1, marketInstruments: 3)); // company story: no market point
        var fresh = StoryRules.Assess(NewsEventType.MarketNews, NewsCategory.General, false, 1, T.AddHours(-1), T, marketInstruments: 2);
        Assert.Equal((AttentionLevel.Medium, 2), (fresh.Level, fresh.Points));
        Assert.Contains("concerns 2 market instruments at once (+1)", fresh.Reasons);
        Assert.Equal(AttentionLevel.Low, StoryRules.Assess(NewsEventType.MarketNews, NewsCategory.General, false, 1, T.AddDays(-2), T, marketInstruments: 2).Level);
    }

    [Fact]
    public void Assess_AddsRecency_LevelsHaveDocumentedThresholds_AndEveryPointHasAReason()
    {
        var fresh = StoryRules.Assess(NewsEventType.InflationRelease, NewsCategory.Macroeconomic, true, 1, T.AddHours(-2), T);
        Assert.Equal((AttentionLevel.High, 4), (fresh.Level, fresh.Points));
        Assert.Equal(3, fresh.Reasons.Count);
        var old = StoryRules.Assess(NewsEventType.InflationRelease, NewsCategory.Macroeconomic, true, 1, T.AddDays(-3), T);
        Assert.Equal((AttentionLevel.Medium, 3), (old.Level, old.Points));
        Assert.Equal(AttentionLevel.Low, StoryRules.Assess(NewsEventType.General, NewsCategory.General, false, 1, T.AddDays(-3), T).Level);
        Assert.Equal(["no attention criterion met"], StoryRules.Assess(NewsEventType.General, NewsCategory.General, false, 1, T.AddDays(-3), T).Reasons);
    }

    [Fact]
    public void SqlFilterThresholds_MatchTheLevelRule_ForEveryCombination()
    {
        foreach (var level in Enum.GetValues<AttentionLevel>())
        {
            var (recent, older) = StoryRules.MinBasePoints(level);
            for (var basePoints = 0; basePoints <= 4; basePoints++)
                foreach (var isRecent in new[] { true, false })
                {
                    var rule = StoryRules.Level(basePoints + (isRecent ? 1 : 0)) >= level;
                    var sql = basePoints >= older || (basePoints >= recent && isRecent);
                    Assert.True(rule == sql, $"{level}, base {basePoints}, recent {isRecent}");
                }
        }
    }

    [Fact]
    public void Cursor_RoundTrips_AndRejectsGarbage()
    {
        var id = Guid.NewGuid();
        Assert.True(StoryRules.TryDecodeCursor(StoryRules.EncodeCursor(T, id), out var at, out var back));
        Assert.Equal((T, id), (at, back));
        Assert.False(StoryRules.TryDecodeCursor("not-a-cursor", out _, out _));
        Assert.False(StoryRules.TryDecodeCursor("", out _, out _));
        Assert.False(StoryRules.TryDecodeCursor(new string('A', 200), out _, out _));
    }

    [Fact]
    public void CentralBankCurrency_CoversTheFourFeeds()
    {
        Assert.Equal("EUR", StoryRules.CentralBankCurrency["EU"]);
        Assert.Equal(["EU", "GB", "JP", "US"], StoryRules.CentralBankCurrency.Keys.Order());
    }
}
