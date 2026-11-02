using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceRetrievalIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HistoricalPrices_Source_RetrievedAtUtc",
                table: "HistoricalPrices",
                columns: new[] { "Source", "RetrievedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HistoricalPrices_Source_RetrievedAtUtc",
                table: "HistoricalPrices");
        }
    }
}
