using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StrategyComparisons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrategyComparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    ProgressMessage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OptimizationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    MlExperimentId = table.Column<Guid>(type: "uuid", nullable: true),
                    MlModelVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationStart = table.Column<DateOnly>(type: "date", nullable: false),
                    EvaluationEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    BenchmarkSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PriceSource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UsesSimulatedData = table.Column<bool>(type: "boolean", nullable: false),
                    DataSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsFair = table.Column<bool>(type: "boolean", nullable: true),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    Warnings = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyComparisons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyComparisons_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StrategyComparisons_MlExperiments_MlExperimentId",
                        column: x => x.MlExperimentId,
                        principalTable: "MlExperiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StrategyComparisons_MlModelVersions_MlModelVersionId",
                        column: x => x.MlModelVersionId,
                        principalTable: "MlModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StrategyComparisons_OptimizationRuns_OptimizationRunId",
                        column: x => x.OptimizationRunId,
                        principalTable: "OptimizationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_MlExperimentId",
                table: "StrategyComparisons",
                column: "MlExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_MlModelVersionId",
                table: "StrategyComparisons",
                column: "MlModelVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_OptimizationRunId",
                table: "StrategyComparisons",
                column: "OptimizationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_StrategyComparisons_OwnerId_CreatedAtUtc",
                table: "StrategyComparisons",
                columns: new[] { "OwnerId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyComparisons");
        }
    }
}
