using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NewsEntityLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntityRuleVersion",
                table: "NewsArticles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NewsArticleEntities",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MatchMethod = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleEntities", x => new { x.ArticleId, x.EntityKey });
                    table.ForeignKey(
                        name: "FK_NewsArticleEntities_NewsArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsStoryEntities",
                columns: table => new
                {
                    StoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStoryEntities", x => new { x.StoryId, x.EntityKey });
                    table.ForeignKey(
                        name: "FK_NewsStoryEntities_NewsStories_StoryId",
                        column: x => x.StoryId,
                        principalTable: "NewsStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_EntityRuleVersion",
                table: "NewsArticles",
                column: "EntityRuleVersion");

            migrationBuilder.CreateIndex(
                name: "IX_NewsStoryEntities_EntityKey_StoryId",
                table: "NewsStoryEntities",
                columns: new[] { "EntityKey", "StoryId" });

            // My Watchlist and portfolio news now find official rate decisions through the central-bank link. Link the stored
            // official central-bank items right away (same rule as NewsEntities.Match), so they do not drop out of those feeds
            // until the scheduled run links every article (EntityRuleVersion stays 0, so it redoes these too).
            migrationBuilder.Sql("""
                INSERT INTO "NewsArticleEntities" ("ArticleId", "EntityKey", "MatchMethod")
                SELECT a."Id", b.key, 'official central-bank feed (' || a."Region" || ')'
                  FROM "NewsArticles" a
                  JOIN (VALUES ('US', 'FED'), ('EU', 'ECB'), ('GB', 'BOE'), ('JP', 'BOJ')) AS b(region, key) ON b.region = a."Region"
                 WHERE a."IsOfficialSource" AND NOT a."IsSimulated" AND a."SourceCategory" = 'CentralBank';
                INSERT INTO "NewsStoryEntities" ("StoryId", "EntityKey")
                SELECT DISTINCT m."StoryId", e."EntityKey"
                  FROM "NewsArticleEntities" e JOIN "NewsStoryArticles" m ON m."ArticleId" = e."ArticleId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsArticleEntities");

            migrationBuilder.DropTable(
                name: "NewsStoryEntities");

            migrationBuilder.DropIndex(
                name: "IX_NewsArticles_EntityRuleVersion",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "EntityRuleVersion",
                table: "NewsArticles");
        }
    }
}
