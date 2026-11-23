using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyFundamentals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FundamentalFacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Concept = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundamentalFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FundamentalFacts_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FundamentalsFetchStates",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundamentalsFetchStates", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_FundamentalsFetchStates_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FundamentalFacts_AssetId_Source_Concept_PeriodType_PeriodEnd",
                table: "FundamentalFacts",
                columns: new[] { "AssetId", "Source", "Concept", "PeriodType", "PeriodEnd" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FundamentalFacts");

            migrationBuilder.DropTable(
                name: "FundamentalsFetchStates");
        }
    }
}
