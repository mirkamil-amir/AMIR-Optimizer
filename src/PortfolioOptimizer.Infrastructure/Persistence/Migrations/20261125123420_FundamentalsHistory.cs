using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FundamentalsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FundamentalFacts_AssetId_Source_Concept_PeriodType_PeriodEnd",
                table: "FundamentalFacts");

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAtUtc",
                table: "FundamentalFacts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                table: "FundamentalFacts",
                type: "timestamp with time zone",
                nullable: true);

            // Stored figures were first seen at most at their last retrieval (earlier refreshes replaced the rows).
            migrationBuilder.Sql("UPDATE \"FundamentalFacts\" SET \"FirstSeenAtUtc\" = \"RetrievedAtUtc\";");

            migrationBuilder.CreateIndex(
                name: "IX_FundamentalFacts_AssetId_Source_Concept_PeriodType_PeriodEnd",
                table: "FundamentalFacts",
                columns: new[] { "AssetId", "Source", "Concept", "PeriodType", "PeriodEnd" },
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Going back drops the history: only current values fit the old unique index.
            migrationBuilder.Sql("DELETE FROM \"FundamentalFacts\" WHERE \"SupersededAtUtc\" IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_FundamentalFacts_AssetId_Source_Concept_PeriodType_PeriodEnd",
                table: "FundamentalFacts");

            migrationBuilder.DropColumn(
                name: "FirstSeenAtUtc",
                table: "FundamentalFacts");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "FundamentalFacts");

            migrationBuilder.CreateIndex(
                name: "IX_FundamentalFacts_AssetId_Source_Concept_PeriodType_PeriodEnd",
                table: "FundamentalFacts",
                columns: new[] { "AssetId", "Source", "Concept", "PeriodType", "PeriodEnd" },
                unique: true);
        }
    }
}
