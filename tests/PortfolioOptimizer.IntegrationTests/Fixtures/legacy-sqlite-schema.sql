CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
    "Timestamp" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "Assets" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Assets" PRIMARY KEY AUTOINCREMENT,
    "Symbol" TEXT NOT NULL,
    "Name" TEXT NULL,
    "Exchange" TEXT NULL,
    "Currency" TEXT NOT NULL,
    "AssetType" TEXT NOT NULL,
    "Sector" TEXT NULL,
    "IsEnabled" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "Backtests" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Backtests" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "StartedAtUtc" TEXT NULL,
    "CompletedAtUtc" TEXT NULL,
    "ConfigJson" TEXT NOT NULL,
    "ResultJson" TEXT NULL,
    "Error" TEXT NULL,
    "PriceSource" TEXT NOT NULL,
    "UsesSimulatedData" INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS "Portfolios" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Portfolios" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "BaseCurrency" TEXT NOT NULL,
    "InitialInvestment" TEXT NOT NULL,
    "CashBalance" TEXT NOT NULL,
    "Settings" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    "Version" INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS "Settings" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
    "ActiveProvider" TEXT NOT NULL,
    "DefaultCurrency" TEXT NOT NULL,
    "DefaultRiskFreeRate" REAL NOT NULL,
    "DefaultBenchmark" TEXT NOT NULL,
    "StalePriceDays" INTEGER NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "SyncJobs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SyncJobs" PRIMARY KEY,
    "Status" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "Symbols" TEXT NOT NULL,
    "StartDate" TEXT NOT NULL,
    "EndDate" TEXT NOT NULL,
    "ForceFullRefresh" INTEGER NOT NULL,
    "RequestedAtUtc" TEXT NOT NULL,
    "StartedAtUtc" TEXT NULL,
    "CompletedAtUtc" TEXT NULL,
    "AssetsTotal" INTEGER NOT NULL,
    "AssetsCompleted" INTEGER NOT NULL,
    "AssetsFailed" INTEGER NOT NULL,
    "Message" TEXT NULL
);
CREATE TABLE IF NOT EXISTS "HistoricalPrices" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_HistoricalPrices" PRIMARY KEY AUTOINCREMENT,
    "AssetId" INTEGER NOT NULL,
    "Date" TEXT NOT NULL,
    "Open" TEXT NULL,
    "High" TEXT NULL,
    "Low" TEXT NULL,
    "Close" TEXT NOT NULL,
    "AdjustedClose" TEXT NULL,
    "Volume" INTEGER NULL,
    "Source" TEXT NOT NULL,
    "IsSimulated" INTEGER NOT NULL,
    "RetrievedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_HistoricalPrices_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "LatestQuotes" (
    "AssetId" INTEGER NOT NULL,
    "Source" TEXT NOT NULL,
    "Price" TEXT NOT NULL,
    "Currency" TEXT NOT NULL,
    "AsOfUtc" TEXT NOT NULL,
    "RetrievedAtUtc" TEXT NOT NULL,
    "IsSimulated" INTEGER NOT NULL,
    CONSTRAINT "PK_LatestQuotes" PRIMARY KEY ("AssetId", "Source"),
    CONSTRAINT "FK_LatestQuotes_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "CashLedger" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CashLedger" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Amount" TEXT NOT NULL,
    "BalanceAfter" TEXT NOT NULL,
    "TimestampUtc" TEXT NOT NULL,
    "ReferenceId" TEXT NULL,
    "Description" TEXT NOT NULL,
    CONSTRAINT "FK_CashLedger_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "Executions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Executions" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "OrderId" TEXT NOT NULL,
    "AssetId" INTEGER NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Side" TEXT NOT NULL,
    "Quantity" TEXT NOT NULL,
    "Price" TEXT NOT NULL,
    "PriceDate" TEXT NOT NULL,
    "GrossAmount" TEXT NOT NULL,
    "Fees" TEXT NOT NULL,
    "NetCashFlow" TEXT NOT NULL,
    "RealizedPnl" TEXT NULL,
    "ExecutedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_Executions_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "Holdings" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Holdings" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "AssetId" INTEGER NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Quantity" TEXT NOT NULL,
    "AverageCost" TEXT NOT NULL,
    "RealizedPnl" TEXT NOT NULL,
    "OpenedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_Holdings_Assets_AssetId" FOREIGN KEY ("AssetId") REFERENCES "Assets" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Holdings_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "OptimizationRuns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_OptimizationRuns" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "Objective" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "Message" TEXT NOT NULL,
    "Symbols" TEXT NOT NULL,
    "PeriodStart" TEXT NULL,
    "PeriodEnd" TEXT NULL,
    "Observations" INTEGER NOT NULL,
    "PriceSource" TEXT NOT NULL,
    "UsesSimulatedData" INTEGER NOT NULL,
    "DataSnapshotHash" TEXT NOT NULL,
    "SettingsSnapshot" TEXT NOT NULL,
    "ExpectedReturn" REAL NULL,
    "Volatility" REAL NULL,
    "SharpeRatio" REAL NULL,
    "SolverIterations" INTEGER NOT NULL,
    "SolverCalls" INTEGER NOT NULL,
    "DurationMs" REAL NOT NULL,
    "Diagnostics" TEXT NOT NULL,
    "AnalyticsJson" TEXT NOT NULL,
    CONSTRAINT "FK_OptimizationRuns_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "SyncLogs" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncLogs" PRIMARY KEY AUTOINCREMENT,
    "JobId" TEXT NULL,
    "AssetId" INTEGER NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "Success" INTEGER NOT NULL,
    "RowsReceived" INTEGER NOT NULL,
    "RowsInserted" INTEGER NOT NULL,
    "RowsUpdated" INTEGER NOT NULL,
    "RowsRejected" INTEGER NOT NULL,
    "DuplicatesSkipped" INTEGER NOT NULL,
    "Message" TEXT NULL,
    "CompletedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_SyncLogs_SyncJobs_JobId" FOREIGN KEY ("JobId") REFERENCES "SyncJobs" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "OptimizationRunWeight" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_OptimizationRunWeight" PRIMARY KEY AUTOINCREMENT,
    "RunId" TEXT NOT NULL,
    "Symbol" TEXT NOT NULL,
    "AssetId" INTEGER NOT NULL,
    "Weight" REAL NOT NULL,
    "ExpectedReturn" REAL NOT NULL,
    "Volatility" REAL NOT NULL,
    "RiskContribution" REAL NULL,
    CONSTRAINT "FK_OptimizationRunWeight_OptimizationRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "OptimizationRuns" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "TradeProposals" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_TradeProposals" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "OptimizationRunId" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "AllowFractionalShares" INTEGER NOT NULL,
    "PortfolioValue" TEXT NOT NULL,
    "CashBefore" TEXT NOT NULL,
    "EstimatedCashAfter" TEXT NOT NULL,
    "EstimatedCosts" TEXT NOT NULL,
    "TargetCashWeight" TEXT NOT NULL,
    "PortfolioVersion" INTEGER NOT NULL,
    "Notes" TEXT NOT NULL,
    CONSTRAINT "FK_TradeProposals_OptimizationRuns_OptimizationRunId" FOREIGN KEY ("OptimizationRunId") REFERENCES "OptimizationRuns" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_TradeProposals_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "PaperTradeBatches" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_PaperTradeBatches" PRIMARY KEY,
    "PortfolioId" TEXT NOT NULL,
    "ProposalId" TEXT NOT NULL,
    "IdempotencyKey" TEXT NOT NULL,
    "ExecutedAtUtc" TEXT NOT NULL,
    "TotalFees" TEXT NOT NULL,
    "CashAfter" TEXT NOT NULL,
    "Notes" TEXT NOT NULL,
    CONSTRAINT "FK_PaperTradeBatches_Portfolios_PortfolioId" FOREIGN KEY ("PortfolioId") REFERENCES "Portfolios" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_PaperTradeBatches_TradeProposals_ProposalId" FOREIGN KEY ("ProposalId") REFERENCES "TradeProposals" ("Id") ON DELETE RESTRICT
);
CREATE TABLE IF NOT EXISTS "TradeProposalLine" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TradeProposalLine" PRIMARY KEY AUTOINCREMENT,
    "ProposalId" TEXT NOT NULL,
    "AssetId" INTEGER NOT NULL,
    "Symbol" TEXT NOT NULL,
    "CompanyName" TEXT NULL,
    "Price" TEXT NULL,
    "PriceDate" TEXT NULL,
    "PriceIsStale" INTEGER NOT NULL,
    "TargetWeight" REAL NOT NULL,
    "CurrentWeight" REAL NOT NULL,
    "CurrentQuantity" TEXT NOT NULL,
    "TargetValue" TEXT NOT NULL,
    "TargetQuantity" TEXT NOT NULL,
    "OrderQuantity" TEXT NOT NULL,
    "Action" TEXT NOT NULL,
    "EstimatedTradeValue" TEXT NOT NULL,
    "EstimatedCost" TEXT NOT NULL,
    "ResultingWeight" REAL NOT NULL,
    "Note" TEXT NULL,
    CONSTRAINT "FK_TradeProposalLine_TradeProposals_ProposalId" FOREIGN KEY ("ProposalId") REFERENCES "TradeProposals" ("Id") ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS "Orders" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Orders" PRIMARY KEY,
    "BatchId" TEXT NOT NULL,
    "PortfolioId" TEXT NOT NULL,
    "ProposalId" TEXT NOT NULL,
    "AssetId" INTEGER NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Side" TEXT NOT NULL,
    "RequestedQuantity" TEXT NOT NULL,
    "FilledQuantity" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "ExecutionPrice" TEXT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "Message" TEXT NULL,
    CONSTRAINT "FK_Orders_PaperTradeBatches_BatchId" FOREIGN KEY ("BatchId") REFERENCES "PaperTradeBatches" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "IX_Assets_Symbol" ON "Assets" ("Symbol");
CREATE INDEX "IX_Backtests_CreatedAtUtc" ON "Backtests" ("CreatedAtUtc");
CREATE INDEX "IX_CashLedger_PortfolioId_TimestampUtc" ON "CashLedger" ("PortfolioId", "TimestampUtc");
CREATE INDEX "IX_Executions_OrderId" ON "Executions" ("OrderId");
CREATE INDEX "IX_Executions_PortfolioId_ExecutedAtUtc" ON "Executions" ("PortfolioId", "ExecutedAtUtc");
CREATE UNIQUE INDEX "IX_HistoricalPrices_AssetId_Source_Date" ON "HistoricalPrices" ("AssetId", "Source", "Date");
CREATE INDEX "IX_Holdings_AssetId" ON "Holdings" ("AssetId");
CREATE UNIQUE INDEX "IX_Holdings_PortfolioId_AssetId" ON "Holdings" ("PortfolioId", "AssetId");
CREATE INDEX "IX_OptimizationRunWeight_RunId" ON "OptimizationRunWeight" ("RunId");
CREATE INDEX "IX_OptimizationRuns_PortfolioId_CreatedAtUtc" ON "OptimizationRuns" ("PortfolioId", "CreatedAtUtc");
CREATE INDEX "IX_Orders_BatchId" ON "Orders" ("BatchId");
CREATE INDEX "IX_Orders_PortfolioId_CreatedAtUtc" ON "Orders" ("PortfolioId", "CreatedAtUtc");
CREATE UNIQUE INDEX "IX_PaperTradeBatches_PortfolioId_IdempotencyKey" ON "PaperTradeBatches" ("PortfolioId", "IdempotencyKey");
CREATE INDEX "IX_PaperTradeBatches_ProposalId" ON "PaperTradeBatches" ("ProposalId");
CREATE INDEX "IX_SyncJobs_RequestedAtUtc" ON "SyncJobs" ("RequestedAtUtc");
CREATE INDEX "IX_SyncLogs_JobId" ON "SyncLogs" ("JobId");
CREATE INDEX "IX_SyncLogs_Source_AssetId_CompletedAtUtc" ON "SyncLogs" ("Source", "AssetId", "CompletedAtUtc");
CREATE INDEX "IX_TradeProposalLine_ProposalId" ON "TradeProposalLine" ("ProposalId");
CREATE INDEX "IX_TradeProposals_OptimizationRunId" ON "TradeProposals" ("OptimizationRunId");
CREATE INDEX "IX_TradeProposals_PortfolioId_CreatedAtUtc" ON "TradeProposals" ("PortfolioId", "CreatedAtUtc");
