namespace PortfolioOptimizer.Domain;

public enum AssetType { Equity, Etf }

public enum ObjectiveKind { MinimumVariance, MaximumSharpe, TargetReturn, MaximumReturnForRisk, EqualWeight }

public enum CovarianceKind { Sample, LedoitWolf }

public enum RebalanceKind { None, Monthly, Quarterly }

public enum OptimizationRunStatus { Succeeded, Infeasible, InvalidInput, Failed }

public enum ProposalStatus { Pending, Executed, Superseded, Cancelled }

public enum OrderSide { Buy, Sell }

public enum ProposalAction { Buy, Sell, Hold, NoPrice }

public enum OrderStatus { Filled, PartiallyFilled, Rejected }

public enum CashEntryType { Deposit, Withdrawal, BuySettlement, SellSettlement, Fee }

public enum JobStatus { Queued, Running, Succeeded, PartiallySucceeded, Failed, Cancelled }
