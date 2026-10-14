using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketUniversesAndQuoteRefresh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PreviousClose",
                table: "LatestQuotes",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Assets",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isin",
                table: "Assets",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mic",
                table: "Assets",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MarketIndices",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BenchmarkSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsHistorical = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceRevision = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Licence = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    VerifiedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketIndices", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "QuoteRefreshRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SymbolsRequested = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteRefreshRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexMemberships",
                columns: table => new
                {
                    IndexCode = table.Column<string>(type: "character varying(20)", nullable: false),
                    AssetId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexMemberships", x => new { x.IndexCode, x.AssetId });
                    table.ForeignKey(
                        name: "FK_IndexMemberships_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IndexMemberships_MarketIndices_IndexCode",
                        column: x => x.IndexCode,
                        principalTable: "MarketIndices",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Country",
                table: "Assets",
                column: "Country");

            migrationBuilder.CreateIndex(
                name: "IX_IndexMemberships_AssetId",
                table: "IndexMemberships",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteRefreshRuns_RequestedAtUtc",
                table: "QuoteRefreshRuns",
                column: "RequestedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexMemberships");

            migrationBuilder.DropTable(
                name: "QuoteRefreshRuns");

            migrationBuilder.DropTable(
                name: "MarketIndices");

            migrationBuilder.DropIndex(
                name: "IX_Assets_Country",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "PreviousClose",
                table: "LatestQuotes");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Isin",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "Mic",
                table: "Assets");
        }
    }
}
