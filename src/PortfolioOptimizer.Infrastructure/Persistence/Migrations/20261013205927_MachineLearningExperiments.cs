using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MachineLearningExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MlExperiments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    ProgressMessage = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ModelFamily = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Target = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HorizonDays = table.Column<int>(type: "integer", nullable: false),
                    FeatureSetVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Symbols = table.Column<List<string>>(type: "text[]", nullable: false),
                    BenchmarkSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PriceSource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UsesSimulatedData = table.Column<bool>(type: "boolean", nullable: false),
                    HistoryStart = table.Column<DateOnly>(type: "date", nullable: false),
                    HistoryEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false),
                    DataSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TrainStart = table.Column<DateOnly>(type: "date", nullable: true),
                    TrainEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidationStart = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidationEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    TestStart = table.Column<DateOnly>(type: "date", nullable: true),
                    TestEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    ValidationSamples = table.Column<int>(type: "integer", nullable: false),
                    TestSamples = table.Column<int>(type: "integer", nullable: false),
                    IsExploratory = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    BacktestJson = table.Column<string>(type: "jsonb", nullable: true),
                    Warnings = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlExperiments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MlExperiments_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MlModelVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ModelFamily = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TrainedFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    TrainedThrough = table.Column<DateOnly>(type: "date", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    HyperparametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    FeatureNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    FeatureSetVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ArtifactPath = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlModelVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MlModelVersions_MlExperiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "MlExperiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MlPredictionSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HorizonDays = table.Column<int>(type: "integer", nullable: false),
                    CashWeight = table.Column<double>(type: "double precision", nullable: false),
                    IntervalMethod = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<List<string>>(type: "text[]", nullable: false),
                    UsesSimulatedData = table.Column<bool>(type: "boolean", nullable: false),
                    PredictionsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlPredictionSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MlPredictionSets_MlExperiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "MlExperiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MlPredictionSets_MlModelVersions_ModelVersionId",
                        column: x => x.ModelVersionId,
                        principalTable: "MlModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MlExperiments_OwnerId_CreatedAtUtc",
                table: "MlExperiments",
                columns: new[] { "OwnerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MlExperiments_Status",
                table: "MlExperiments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MlModelVersions_ExperimentId_Version",
                table: "MlModelVersions",
                columns: new[] { "ExperimentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MlPredictionSets_ExperimentId_CreatedAtUtc",
                table: "MlPredictionSets",
                columns: new[] { "ExperimentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MlPredictionSets_ModelVersionId",
                table: "MlPredictionSets",
                column: "ModelVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MlPredictionSets");

            migrationBuilder.DropTable(
                name: "MlModelVersions");

            migrationBuilder.DropTable(
                name: "MlExperiments");
        }
    }
}
