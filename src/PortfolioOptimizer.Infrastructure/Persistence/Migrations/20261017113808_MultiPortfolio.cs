using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiPortfolio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PortfolioId",
                table: "ResearchSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "Portfolios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Portfolios",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssetId = table.Column<int>(type: "integer", nullable: true),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FxRate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    FxRateDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FxSource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Fees = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    CashAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BookedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CashEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversesId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioTransactions", x => x.Id);
                    table.CheckConstraint("CK_PortfolioTransactions_Fees", "\"Fees\" >= 0");
                    table.CheckConstraint("CK_PortfolioTransactions_FxRate", "\"FxRate\" > 0");
                    table.ForeignKey(
                        name: "FK_PortfolioTransactions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortfolioTransactions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSessions_PortfolioId",
                table: "ResearchSessions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTransactions_AssetId",
                table: "PortfolioTransactions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTransactions_PortfolioId_BookedAtUtc",
                table: "PortfolioTransactions",
                columns: new[] { "PortfolioId", "BookedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTransactions_PortfolioId_IdempotencyKey",
                table: "PortfolioTransactions",
                columns: new[] { "PortfolioId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioTransactions_ReversesId",
                table: "PortfolioTransactions",
                column: "ReversesId",
                unique: true,
                filter: "\"ReversesId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ResearchSessions_Portfolios_PortfolioId",
                table: "ResearchSessions",
                column: "PortfolioId",
                principalTable: "Portfolios",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResearchSessions_Portfolios_PortfolioId",
                table: "ResearchSessions");

            migrationBuilder.DropTable(
                name: "PortfolioTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ResearchSessions_PortfolioId",
                table: "ResearchSessions");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "ResearchSessions");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Portfolios");
        }
    }
}
