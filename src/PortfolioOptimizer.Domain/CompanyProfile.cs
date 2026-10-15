namespace PortfolioOptimizer.Domain;

/// <summary>Outcome of the last attempt to obtain a company description.</summary>
public enum CompanyProfileStatus
{
    /// <summary>A description was retrieved from the source.</summary>
    Available,
    /// <summary>The source has no usable description for this company (missing page, disambiguation page, empty text).</summary>
    NotFound,
    /// <summary>The source could not be reached or answered with an error/rate limit.</summary>
    Error,
}

/// <summary>
/// Cached company description per listing, kept separately from prices because it changes rarely. Text is stored exactly
/// as supplied by the source (never generated) together with its source, URL, licence and retrieval time.
/// </summary>
public class CompanyProfile
{
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    /// <summary>Reference the profile was resolved from (e.g. "en:Apple Inc."); a changed catalogue reference invalidates the cache.</summary>
    public required string Reference { get; set; }
    public CompanyProfileStatus Status { get; set; }
    public string? Description { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public string? Licence { get; set; }
    /// <summary>ISO 639-1 language of the text.</summary>
    public string? Language { get; set; }
    /// <summary>When the stored text was retrieved (only set for <see cref="CompanyProfileStatus.Available"/>).</summary>
    public DateTime? RetrievedAtUtc { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public string? LastError { get; set; }
}
