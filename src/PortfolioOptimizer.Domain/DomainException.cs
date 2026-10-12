namespace PortfolioOptimizer.Domain;

/// <summary>A business rule was violated (e.g. insufficient cash, selling more shares than held).</summary>
public class DomainException(string message) : Exception(message);
