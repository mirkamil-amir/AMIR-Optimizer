using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceCoverage",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Observations = table.Column<int>(type: "integer", nullable: false),
                    FirstDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LastRetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdjustedObservations = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCoverage", x => new { x.AssetId, x.Source });
                    table.ForeignKey(
                        name: "FK_PriceCoverage_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceCoverage");
        }
    }
}
