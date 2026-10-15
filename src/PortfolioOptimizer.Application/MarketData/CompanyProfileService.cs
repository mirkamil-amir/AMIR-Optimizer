using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.MarketData;

/// <summary>
/// Company description of a listing with its source.
/// <see cref="Status"/>: Available, NotFound (the source has no usable text), NoReference (the catalogue has no article
/// for this listing), Error (the source failed and nothing is cached), NotConfigured (no source enabled).
/// When a refresh fails but an earlier description exists, it is returned with <see cref="IsStale"/> and a message.
/// </summary>
public sealed record CompanyProfileDto(
    string Symbol,
    string Status,
    string? Description,
    string? SourceName,
    string? SourceUrl,
    string? Licence,
    string? Language,
    DateTime? RetrievedAtUtc,
    bool IsStale,
    bool IsSimulated,
    string? Message);

/// <summary>
/// Serves company descriptions from a cache table (<see cref="CompanyProfile"/>), refreshing from the configured provider
/// only when the cached entry is old: descriptions 30 days, "not found" 7 days, errors retried after 15 minutes.
/// One fetch per listing at a time; a provider failure never removes an earlier description and never yields invented text.
/// </summary>
public sealed class CompanyProfileService(
    IApplicationDbContext db,
    IEnumerable<ICompanyProfileProvider> providers,
    TimeProvider clock,
    ILogger<CompanyProfileService> logger)
{
    public static readonly TimeSpan AvailableTtl = TimeSpan.FromDays(30);
    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan ErrorRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();

    public async Task<CompanyProfileDto> GetAsync(string symbol, CancellationToken ct)
    {
        var s = Asset.NormalizeSymbol(symbol);
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Symbol == s, ct) ?? throw new NotFoundException("Stock", s);
        if (asset.AssetType != AssetType.Equity)
            return Empty(asset, "NoReference", "Company descriptions are only available for individual companies, not for indices or funds.");
        if (string.IsNullOrWhiteSpace(asset.ReferenceArticle))
            return Empty(asset, "NoReference", "Company description is currently unavailable: the catalogue source has no article for this listing.");
        var provider = providers.FirstOrDefault(p => p.IsConfigured);
        var cached = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.AssetId == asset.Id, ct);
        if (cached is not null && cached.Reference == asset.ReferenceArticle && (provider is null || (!IsDue(cached) && SameSource(cached, provider))))
            return ToDto(asset, cached, provider);
        if (provider is null)
            return cached is { Status: CompanyProfileStatus.Available }
                ? ToDto(asset, cached, null)
                : Empty(asset, "NotConfigured", "Company description is currently unavailable: no description source is enabled.");

        var gate = Locks.GetOrAdd(asset.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct); // concurrent requests for the same stock wait for one fetch instead of starting another
        try
        {
            var entry = await db.CompanyProfiles.SingleOrDefaultAsync(p => p.AssetId == asset.Id, ct);
            if (entry is not null && entry.Reference == asset.ReferenceArticle && !IsDue(entry) && SameSource(entry, provider)) return ToDto(asset, entry, provider);
            if (entry is null)
            {
                entry = new CompanyProfile { AssetId = asset.Id, Reference = asset.ReferenceArticle, Status = CompanyProfileStatus.Error };
                db.CompanyProfiles.Add(entry);
            }
            else if (entry.Reference != asset.ReferenceArticle || !SameSource(entry, provider))
            {
                // Another article or another source (e.g. simulated → Wikipedia): the old text must not be shown for it.
                entry.Reference = asset.ReferenceArticle;
                entry.Status = CompanyProfileStatus.Error;
                entry.Description = entry.SourceUrl = entry.Language = null;
                entry.RetrievedAtUtc = null;
            }
            entry.AttemptedAtUtc = clock.GetUtcNow().UtcDateTime;
            entry.SourceName = provider.SourceName;
            entry.Licence = provider.Licence;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(FetchTimeout);
                var result = await provider.GetAsync(asset.ReferenceArticle, timeout.Token);
                entry.SourceName = provider.SourceName;
                entry.Licence = provider.Licence;
                entry.Language = result.Language;
                entry.SourceUrl = result.SourceUrl;
                entry.LastError = result.Found ? null : Truncate(result.NotFoundReason ?? "No description available.", 300);
                if (result.Found && !string.IsNullOrWhiteSpace(result.Description))
                {
                    entry.Status = CompanyProfileStatus.Available;
                    entry.Description = Truncate(result.Description.Trim(), 2000);
                    entry.RetrievedAtUtc = entry.AttemptedAtUtc;
                }
                else
                {
                    entry.Status = CompanyProfileStatus.NotFound;
                    entry.Description = null;
                    entry.RetrievedAtUtc = null;
                }
            }
            catch (Exception ex) when (ex is CompanyProfileProviderException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                var message = ex is OperationCanceledException ? "The description source did not answer in time." : ex.Message;
                logger.LogWarning("Company profile for {Symbol} ({Reference}) failed: {Message}", asset.Symbol, asset.ReferenceArticle, message);
                entry.LastError = Truncate(message, 300);
                // Keep an earlier description (served as stale); otherwise record the error.
                if (entry.Status != CompanyProfileStatus.Available || entry.Description is null) entry.Status = CompanyProfileStatus.Error;
            }
            await db.SaveChangesAsync(ct);
            return ToDto(asset, entry, provider);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A cached entry from another source (e.g. the simulated test provider) is never served by the configured one.</summary>
    private static bool SameSource(CompanyProfile p, ICompanyProfileProvider provider) => p.SourceName is null || p.SourceName == provider.SourceName;

    private bool IsDue(CompanyProfile p)
    {
        var age = clock.GetUtcNow().UtcDateTime - p.AttemptedAtUtc;
        if (p.LastError is not null && p.Status != CompanyProfileStatus.NotFound) return age >= ErrorRetry; // failed refresh
        return p.Status switch
        {
            CompanyProfileStatus.Available => age >= AvailableTtl,
            CompanyProfileStatus.NotFound => age >= NotFoundTtl,
            _ => age >= ErrorRetry,
        };
    }

    private static CompanyProfileDto ToDto(Asset asset, CompanyProfile p, ICompanyProfileProvider? provider)
    {
        var simulated = provider is not null && provider.Name.StartsWith("Mock", StringComparison.Ordinal);
        return p.Status switch
        {
            CompanyProfileStatus.Available => new CompanyProfileDto(asset.Symbol, "Available", p.Description, p.SourceName, p.SourceUrl, p.Licence, p.Language,
                p.RetrievedAtUtc, p.LastError is not null, simulated,
                p.LastError is null ? null : $"Showing the description retrieved on {p.RetrievedAtUtc:yyyy-MM-dd}; the latest refresh failed ({p.LastError})."),
            CompanyProfileStatus.NotFound => new CompanyProfileDto(asset.Symbol, "NotFound", null, p.SourceName, p.SourceUrl, p.Licence, p.Language, null, false, simulated,
                $"Company description is currently unavailable: {p.LastError ?? "the source has no description for this company."}"),
            _ => new CompanyProfileDto(asset.Symbol, "Error", null, p.SourceName, null, p.Licence, null, null, false, simulated,
                $"The description source could not be reached ({p.LastError ?? "unknown error"}). It will be retried in a few minutes."),
        };
    }

    private static CompanyProfileDto Empty(Asset asset, string status, string message) =>
        new(asset.Symbol, status, null, null, null, null, null, null, false, false, message);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
