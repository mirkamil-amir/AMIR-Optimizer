using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WatchlistOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "WatchlistEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing lists keep the order they had (oldest first): 0, 1, 2 … per owner. Changes no other data.
            migrationBuilder.Sql("""
                UPDATE "WatchlistEntries" AS w
                SET "Position" = o.rn - 1
                FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "OwnerId" ORDER BY "AddedAtUtc", "Id") AS rn
                      FROM "WatchlistEntries") AS o
                WHERE w."Id" = o."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Position",
                table: "WatchlistEntries");
        }
    }
}
