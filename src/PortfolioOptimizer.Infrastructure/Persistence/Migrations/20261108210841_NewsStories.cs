using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NewsStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsStories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Headline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EventType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EventTypeBasis = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarketArea = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    FirstPublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArticleCount = table.Column<int>(type: "integer", nullable: false),
                    PublisherCount = table.Column<int>(type: "integer", nullable: false),
                    HasOfficialSource = table.Column<bool>(type: "boolean", nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    BasePoints = table.Column<int>(type: "integer", nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsStoryArticles",
                columns: table => new
                {
                    StoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStoryArticles", x => new { x.StoryId, x.ArticleId });
                    table.ForeignKey(
                        name: "FK_NewsStoryArticles_NewsArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsStoryArticles_NewsStories_StoryId",
                        column: x => x.StoryId,
                        principalTable: "NewsStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsStoryAssets",
                columns: table => new
                {
                    StoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsStoryAssets", x => new { x.StoryId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_NewsStoryAssets_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsStoryAssets_NewsStories_StoryId",
                        column: x => x.StoryId,
                        principalTable: "NewsStories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsStories_LastPublishedAtUtc_Id",
                table: "NewsStories",
                columns: new[] { "LastPublishedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsStories_Scope_LastPublishedAtUtc_Id",
                table: "NewsStories",
                columns: new[] { "Scope", "LastPublishedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsStoryArticles_ArticleId",
                table: "NewsStoryArticles",
                column: "ArticleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsStoryAssets_AssetId_StoryId",
                table: "NewsStoryAssets",
                columns: new[] { "AssetId", "StoryId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsStoryArticles");

            migrationBuilder.DropTable(
                name: "NewsStoryAssets");

            migrationBuilder.DropTable(
                name: "NewsStories");
        }
    }
}
