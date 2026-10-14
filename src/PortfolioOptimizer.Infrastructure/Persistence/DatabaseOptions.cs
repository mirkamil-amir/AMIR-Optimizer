namespace PortfolioOptimizer.Infrastructure.Persistence;

/// <summary>
/// Database configuration. The connection string is read from <c>ConnectionStrings:DefaultConnection</c>
/// (environment variable <c>ConnectionStrings__DefaultConnection</c>); pooling, timeouts and TLS are set there
/// using standard Npgsql keywords (e.g. <c>Maximum Pool Size=50;Timeout=15;SSL Mode=VerifyFull</c>).
/// </summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>
    /// Apply pending EF Core migrations when the API starts. Convenient for local development; disable it in deployed
    /// environments and run the dedicated <c>migrate</c> command with a privileged migration identity instead.
    /// When disabled and migrations are pending, the API refuses to start.
    /// </summary>
    public bool MigrateOnStartup { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 60;
}
