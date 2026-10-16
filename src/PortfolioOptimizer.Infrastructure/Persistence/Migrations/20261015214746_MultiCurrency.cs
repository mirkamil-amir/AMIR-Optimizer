using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "TradeProposalLine",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "TradeProposalLine",
                type: "numeric(20,10)",
                precision: 20,
                scale: 10,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FxRateDate",
                table: "TradeProposalLine",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "StrategyComparisons",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FxSource",
                table: "StrategyComparisons",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Orders",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "Orders",
                type: "numeric(20,10)",
                precision: 20,
                scale: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "OptimizationRuns",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FxSource",
                table: "OptimizationRuns",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "MlExperiments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FxSource",
                table: "MlExperiments",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Executions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "Executions",
                type: "numeric(20,10)",
                precision: 20,
                scale: 10,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FxRateDate",
                table: "Executions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FxSource",
                table: "Executions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Backtests",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FxSource",
                table: "Backtests",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FxRates",
                columns: table => new
                {
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    UnitsPerEur = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRates", x => new { x.Source, x.Currency, x.Date });
                    table.CheckConstraint("CK_FxRates_NotEur", "\"Currency\" <> 'EUR'");
                    table.CheckConstraint("CK_FxRates_Positive", "\"UnitsPerEur\" > 0");
                });

            // Backfill: before this migration every analysis and trade used a single currency (the restrictions in
            // docs/currency-audit.md), so existing rows are in their instruments' own currency with an implicit rate of 1.
            migrationBuilder.Sql("""
                UPDATE "Executions" e SET "Currency" = a."Currency" FROM "Assets" a WHERE a."Id" = e."AssetId";
                UPDATE "Orders" o SET "Currency" = a."Currency", "FxRate" = CASE WHEN o."ExecutionPrice" IS NULL THEN NULL ELSE 1 END
                    FROM "Assets" a WHERE a."Id" = o."AssetId";
                UPDATE "TradeProposalLine" l SET "Currency" = a."Currency", "FxRate" = CASE WHEN l."Price" IS NULL THEN NULL ELSE 1 END
                    FROM "Assets" a WHERE a."Id" = l."AssetId";
                UPDATE "OptimizationRuns" r SET "Currency" = COALESCE(
                    (SELECT a."Currency" FROM "OptimizationRunWeight" w JOIN "Assets" a ON a."Id" = w."AssetId" WHERE w."RunId" = r."Id" LIMIT 1),
                    (SELECT a."Currency" FROM "Assets" a WHERE a."Symbol" = r."Symbols"[1]),
                    (SELECT p."BaseCurrency" FROM "Portfolios" p WHERE p."Id" = r."PortfolioId"),
                    'USD');
                UPDATE "Backtests" b SET "Currency" = COALESCE(
                    (SELECT a."Currency" FROM "Assets" a WHERE a."Symbol" = (b."ConfigJson"::jsonb -> 'universe' ->> 0)), 'USD');
                UPDATE "StrategyComparisons" c SET "Currency" = COALESCE((SELECT a."Currency" FROM "Assets" a WHERE a."Symbol" = c."BenchmarkSymbol"), 'USD');
                UPDATE "MlExperiments" m SET "Currency" = COALESCE((SELECT a."Currency" FROM "Assets" a WHERE a."Symbol" = m."BenchmarkSymbol"), 'USD');
                UPDATE "Executions" SET "Currency" = 'USD' WHERE "Currency" = '';
                UPDATE "Orders" SET "Currency" = 'USD' WHERE "Currency" = '';
                UPDATE "TradeProposalLine" SET "Currency" = 'USD' WHERE "Currency" = '';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TradeExecutions_FxRate",
                table: "Executions",
                sql: "\"FxRate\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FxRates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TradeExecutions_FxRate",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "TradeProposalLine");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "TradeProposalLine");

            migrationBuilder.DropColumn(
                name: "FxRateDate",
                table: "TradeProposalLine");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "StrategyComparisons");

            migrationBuilder.DropColumn(
                name: "FxSource",
                table: "StrategyComparisons");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "OptimizationRuns");

            migrationBuilder.DropColumn(
                name: "FxSource",
                table: "OptimizationRuns");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "MlExperiments");

            migrationBuilder.DropColumn(
                name: "FxSource",
                table: "MlExperiments");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "FxRateDate",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "FxSource",
                table: "Executions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Backtests");

            migrationBuilder.DropColumn(
                name: "FxSource",
                table: "Backtests");
        }
    }
}
