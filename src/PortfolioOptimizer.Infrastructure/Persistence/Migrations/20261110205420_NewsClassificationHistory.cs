using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NewsClassificationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceCategory",
                table: "NewsArticles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceEventType",
                table: "NewsArticles",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceEventTypeBasis",
                table: "NewsArticles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NewsArticleClassifications",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CategoryBasis = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    EventTypeBasis = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarketArea = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleClassifications", x => new { x.ArticleId, x.RuleVersion });
                    table.ForeignKey(
                        name: "FK_NewsArticleClassifications_NewsArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_RuleVersion",
                table: "NewsArticles",
                column: "RuleVersion");

            // What the source itself stated, recovered from stored articles (additive; nothing existing is changed):
            // official feeds kept their own category with this basis; only SEC filings carry their own event type.
            migrationBuilder.Sql("""
                UPDATE "NewsArticles" SET "SourceCategory" = "Category"
                 WHERE "CategoryBasis" = 'category of the official source feed';
                UPDATE "NewsArticles" SET "SourceEventType" = "EventType", "SourceEventTypeBasis" = "EventTypeBasis"
                 WHERE "Provider" = 'SecEdgar' AND "EventType" IS NOT NULL;
                """);

            // History starts with every stored article's current classification. Version 1 had no processing time:
            // articles were classified when retrieved.
            migrationBuilder.Sql("""
                INSERT INTO "NewsArticleClassifications"
                    ("ArticleId", "RuleVersion", "Category", "CategoryBasis", "Scope", "EventType", "EventTypeBasis", "MarketArea",
                     "ProcessedAtUtc", "Trigger", "Note")
                SELECT "Id", "RuleVersion", "Category", "CategoryBasis", "Scope", "EventType", "EventTypeBasis", "MarketArea",
                       COALESCE("ProcessedAtUtc", "RetrievedAtUtc"), 'Backfill',
                       CASE WHEN "ProcessedAtUtc" IS NULL
                            THEN 'Recorded when the history was introduced; classified at retrieval.'
                            ELSE 'Recorded when the history was introduced.' END
                  FROM "NewsArticles";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsArticleClassifications");

            migrationBuilder.DropIndex(
                name: "IX_NewsArticles_RuleVersion",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "SourceCategory",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "SourceEventType",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "SourceEventTypeBasis",
                table: "NewsArticles");
        }
    }
}
