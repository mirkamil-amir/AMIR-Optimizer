namespace PortfolioOptimizer.Domain;

/// <summary>
/// A stock a user follows. Not a holding: adding or removing an entry never touches portfolios, runs or shared market
/// data. One row per (owner, listing); the listing is the catalogue <see cref="Asset"/> (symbol incl. venue suffix),
/// so the same company on two venues is two entries and a ticker is never treated as globally unique.
/// </summary>
public class WatchlistEntry
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public DateTime AddedAtUtc { get; set; }
}
