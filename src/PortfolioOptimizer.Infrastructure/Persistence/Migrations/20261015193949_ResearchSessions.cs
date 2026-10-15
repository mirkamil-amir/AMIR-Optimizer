using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResearchSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BriefJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidatesJson = table.Column<string>(type: "jsonb", nullable: false),
                    HypothesesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PendingRefinementJson = table.Column<string>(type: "jsonb", nullable: true),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    LlmInputTokens = table.Column<int>(type: "integer", nullable: false),
                    LlmOutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchSessions_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResearchEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchEvents_ResearchSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ResearchSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvents_SessionId_Id",
                table: "ResearchEvents",
                columns: new[] { "SessionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSessions_OwnerId_UpdatedAtUtc",
                table: "ResearchSessions",
                columns: new[] { "OwnerId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResearchEvents");

            migrationBuilder.DropTable(
                name: "ResearchSessions");
        }
    }
}
