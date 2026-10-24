using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StrategiesPerPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PortfolioId",
                table: "StrategyComparisons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PortfolioId",
                table: "MlExperiments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PortfolioId",
                table: "Backtests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_OwnerId_PortfolioId",
                table: "StrategyComparisons",
                columns: new[] { "OwnerId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_PortfolioId",
                table: "StrategyComparisons",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_MlExperiments_OwnerId_PortfolioId",
                table: "MlExperiments",
                columns: new[] { "OwnerId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_MlExperiments_PortfolioId",
                table: "MlExperiments",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_Backtests_OwnerId_PortfolioId",
                table: "Backtests",
                columns: new[] { "OwnerId", "PortfolioId" });

            migrationBuilder.CreateIndex(
                name: "IX_Backtests_PortfolioId",
                table: "Backtests",
                column: "PortfolioId");

            migrationBuilder.AddForeignKey(
                name: "FK_Backtests_Portfolios_PortfolioId",
                table: "Backtests",
                column: "PortfolioId",
                principalTable: "Portfolios",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MlExperiments_Portfolios_PortfolioId",
                table: "MlExperiments",
                column: "PortfolioId",
                principalTable: "Portfolios",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_StrategyComparisons_Portfolios_PortfolioId",
                table: "StrategyComparisons",
                column: "PortfolioId",
                principalTable: "Portfolios",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Backtests_Portfolios_PortfolioId",
                table: "Backtests");

            migrationBuilder.DropForeignKey(
                name: "FK_MlExperiments_Portfolios_PortfolioId",
                table: "MlExperiments");

            migrationBuilder.DropForeignKey(
                name: "FK_StrategyComparisons_Portfolios_PortfolioId",
                table: "StrategyComparisons");

            migrationBuilder.DropIndex(
                name: "IX_StrategyComparisons_OwnerId_PortfolioId",
                table: "StrategyComparisons");

            migrationBuilder.DropIndex(
                name: "IX_StrategyComparisons_PortfolioId",
                table: "StrategyComparisons");

            migrationBuilder.DropIndex(
                name: "IX_MlExperiments_OwnerId_PortfolioId",
                table: "MlExperiments");

            migrationBuilder.DropIndex(
                name: "IX_MlExperiments_PortfolioId",
                table: "MlExperiments");

            migrationBuilder.DropIndex(
                name: "IX_Backtests_OwnerId_PortfolioId",
                table: "Backtests");

            migrationBuilder.DropIndex(
                name: "IX_Backtests_PortfolioId",
                table: "Backtests");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "StrategyComparisons");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "MlExperiments");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "Backtests");
        }
    }
}
