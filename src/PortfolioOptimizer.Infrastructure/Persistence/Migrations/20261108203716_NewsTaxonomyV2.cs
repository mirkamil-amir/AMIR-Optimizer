using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NewsTaxonomyV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "NewsArticles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventTypeBasis",
                table: "NewsArticles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketArea",
                table: "NewsArticles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessedAtUtc",
                table: "NewsArticles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RuleVersion",
                table: "NewsArticles",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventType",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "EventTypeBasis",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "MarketArea",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "ProcessedAtUtc",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "RuleVersion",
                table: "NewsArticles");
        }
    }
}
