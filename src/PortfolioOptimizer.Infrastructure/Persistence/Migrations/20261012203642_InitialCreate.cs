using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Exchange = table.Column<string>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    AssetType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Sector = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Backtests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    PriceSource = table.Column<string>(type: "TEXT", nullable: false),
                    UsesSimulatedData = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Backtests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Portfolios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseCurrency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    InitialInvestment = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    CashBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Portfolios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveProvider = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultCurrency = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultRiskFreeRate = table.Column<double>(type: "REAL", nullable: false),
                    DefaultBenchmark = table.Column<string>(type: "TEXT", nullable: false),
                    StalePriceDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Symbols = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ForceFullRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AssetsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    AssetsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    AssetsFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Open = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    High = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    Low = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    Close = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    AdjustedClose = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    Volume = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsSimulated = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricalPrices_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LatestQuotes",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsSimulated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LatestQuotes", x => new { x.AssetId, x.Source });
                    table.ForeignKey(
                        name: "FK_LatestQuotes_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CashLedger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashLedger_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    PriceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Fees = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    NetCashFlow = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    ExecutedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Executions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Holdings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    AverageCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holdings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Holdings_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Holdings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Objective = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Symbols = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Observations = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceSource = table.Column<string>(type: "TEXT", nullable: false),
                    UsesSimulatedData = table.Column<bool>(type: "INTEGER", nullable: false),
                    DataSnapshotHash = table.Column<string>(type: "TEXT", nullable: false),
                    SettingsSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedReturn = table.Column<double>(type: "REAL", nullable: true),
                    Volatility = table.Column<double>(type: "REAL", nullable: true),
                    SharpeRatio = table.Column<double>(type: "REAL", nullable: true),
                    SolverIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    SolverCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyticsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptimizationRuns_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    RowsReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsInserted = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsUpdated = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsRejected = table.Column<int>(type: "INTEGER", nullable: false),
                    DuplicatesSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncLogs_SyncJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "SyncJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationRunWeight",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Weight = table.Column<double>(type: "REAL", nullable: false),
                    ExpectedReturn = table.Column<double>(type: "REAL", nullable: false),
                    Volatility = table.Column<double>(type: "REAL", nullable: false),
                    RiskContribution = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRunWeight", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptimizationRunWeight_OptimizationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "OptimizationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TradeProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OptimizationRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AllowFractionalShares = table.Column<bool>(type: "INTEGER", nullable: false),
                    PortfolioValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    CashBefore = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    EstimatedCashAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    EstimatedCosts = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TargetCashWeight = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    PortfolioVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeProposals_OptimizationRuns_OptimizationRunId",
                        column: x => x.OptimizationRunId,
                        principalTable: "OptimizationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TradeProposals_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaperTradeBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExecutedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalFees = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    CashAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTradeBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaperTradeBatches_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaperTradeBatches_TradeProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "TradeProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TradeProposalLine",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: true),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    PriceDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PriceIsStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetWeight = table.Column<double>(type: "REAL", nullable: false),
                    CurrentWeight = table.Column<double>(type: "REAL", nullable: false),
                    CurrentQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TargetValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    TargetQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OrderQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    EstimatedTradeValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    ResultingWeight = table.Column<double>(type: "REAL", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeProposalLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradeProposalLine_TradeProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "TradeProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ExecutionPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Orders_PaperTradeBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "PaperTradeBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Symbol",
                table: "Assets",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Backtests_CreatedAtUtc",
                table: "Backtests",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedger_PortfolioId_TimestampUtc",
                table: "CashLedger",
                columns: new[] { "PortfolioId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Executions_OrderId",
                table: "Executions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Executions_PortfolioId_ExecutedAtUtc",
                table: "Executions",
                columns: new[] { "PortfolioId", "ExecutedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalPrices_AssetId_Source_Date",
                table: "HistoricalPrices",
                columns: new[] { "AssetId", "Source", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_AssetId",
                table: "Holdings",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Holdings_PortfolioId_AssetId",
                table: "Holdings",
                columns: new[] { "PortfolioId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRunWeight_RunId",
                table: "OptimizationRunWeight",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRuns_PortfolioId_CreatedAtUtc",
                table: "OptimizationRuns",
                columns: new[] { "PortfolioId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BatchId",
                table: "Orders",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PortfolioId_CreatedAtUtc",
                table: "Orders",
                columns: new[] { "PortfolioId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaperTradeBatches_PortfolioId_IdempotencyKey",
                table: "PaperTradeBatches",
                columns: new[] { "PortfolioId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaperTradeBatches_ProposalId",
                table: "PaperTradeBatches",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_RequestedAtUtc",
                table: "SyncJobs",
                column: "RequestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogs_JobId",
                table: "SyncLogs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogs_Source_AssetId_CompletedAtUtc",
                table: "SyncLogs",
                columns: new[] { "Source", "AssetId", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeProposalLine_ProposalId",
                table: "TradeProposalLine",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeProposals_OptimizationRunId",
                table: "TradeProposals",
                column: "OptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeProposals_PortfolioId_CreatedAtUtc",
                table: "TradeProposals",
                columns: new[] { "PortfolioId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Backtests");

            migrationBuilder.DropTable(
                name: "CashLedger");

            migrationBuilder.DropTable(
                name: "Executions");

            migrationBuilder.DropTable(
                name: "HistoricalPrices");

            migrationBuilder.DropTable(
                name: "Holdings");

            migrationBuilder.DropTable(
                name: "LatestQuotes");

            migrationBuilder.DropTable(
                name: "OptimizationRunWeight");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SyncLogs");

            migrationBuilder.DropTable(
                name: "TradeProposalLine");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "PaperTradeBatches");

            migrationBuilder.DropTable(
                name: "SyncJobs");

            migrationBuilder.DropTable(
                name: "TradeProposals");

            migrationBuilder.DropTable(
                name: "OptimizationRuns");

            migrationBuilder.DropTable(
                name: "Portfolios");
        }
    }
}
