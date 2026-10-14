using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FinancialNews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NewsArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProviderArticleId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeadlineKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Headline = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetrievedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Summary = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CategoryBasis = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsOfficialSource = table.Column<bool>(type: "boolean", nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsFetchStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastArticleCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFetchStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "NewsArticleAssets",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<int>(type: "integer", nullable: false),
                    MatchMethod = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsArticleAssets", x => new { x.ArticleId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_NewsArticleAssets_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsArticleAssets_NewsArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "NewsArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticleAssets_AssetId",
                table: "NewsArticleAssets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_Fingerprint",
                table: "NewsArticles",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_HeadlineKey",
                table: "NewsArticles",
                column: "HeadlineKey");

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_PublishedAtUtc",
                table: "NewsArticles",
                column: "PublishedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsArticleAssets");

            migrationBuilder.DropTable(
                name: "NewsFetchStates");

            migrationBuilder.DropTable(
                name: "NewsArticles");
        }
    }
}
