using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MatrixPilot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatrixPilots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Effort = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EnabledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDecisionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulDecisionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextDecisionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PauseReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ActiveCycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveCycleStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BaselineJson = table.Column<string>(type: "jsonb", nullable: true),
                    BaselineAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatrixPilots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatrixPilots_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MatrixPilots_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatrixPilotCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Phase = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OutcomeReason = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    MandateVersion = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModelReported = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Effort = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToolsVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ModelCalls = table.Column<int>(type: "integer", nullable: false),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    DataMs = table.Column<double>(type: "double precision", nullable: false),
                    ToolMs = table.Column<double>(type: "double precision", nullable: false),
                    ModelMs = table.Column<double>(type: "double precision", nullable: false),
                    ValidationMs = table.Column<double>(type: "double precision", nullable: false),
                    ExecutionMs = table.Column<double>(type: "double precision", nullable: false),
                    TotalMs = table.Column<double>(type: "double precision", nullable: false),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: false),
                    DecisionJson = table.Column<string>(type: "jsonb", nullable: true),
                    CritiqueJson = table.Column<string>(type: "jsonb", nullable: true),
                    ValidationJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExecutedJson = table.Column<string>(type: "jsonb", nullable: true),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    TradeBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    TradesBooked = table.Column<int>(type: "integer", nullable: false),
                    Fees = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: false),
                    NarrativeJson = table.Column<string>(type: "jsonb", nullable: true),
                    NarrativeSource = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatrixPilotCycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatrixPilotCycles_MatrixPilots_PilotId",
                        column: x => x.PilotId,
                        principalTable: "MatrixPilots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatrixPilotEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    NewStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Reason = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatrixPilotEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatrixPilotEvents_MatrixPilots_PilotId",
                        column: x => x.PilotId,
                        principalTable: "MatrixPilots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatrixPilotMandates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    MandateJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatrixPilotMandates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatrixPilotMandates_MatrixPilots_PilotId",
                        column: x => x.PilotId,
                        principalTable: "MatrixPilots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatrixPilotSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: true),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    OutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatrixPilotSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatrixPilotSteps_MatrixPilotCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "MatrixPilotCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotCycles_Outcome",
                table: "MatrixPilotCycles",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotCycles_PilotId_CreatedAtUtc",
                table: "MatrixPilotCycles",
                columns: new[] { "PilotId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotCycles_PilotId_CycleKey",
                table: "MatrixPilotCycles",
                columns: new[] { "PilotId", "CycleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotEvents_PilotId_Id",
                table: "MatrixPilotEvents",
                columns: new[] { "PilotId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotMandates_PilotId_Version",
                table: "MatrixPilotMandates",
                columns: new[] { "PilotId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilotSteps_CycleId_Sequence",
                table: "MatrixPilotSteps",
                columns: new[] { "CycleId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilots_OwnerId_Status",
                table: "MatrixPilots",
                columns: new[] { "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilots_PortfolioId",
                table: "MatrixPilots",
                column: "PortfolioId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatrixPilots_Status_NextDecisionAtUtc",
                table: "MatrixPilots",
                columns: new[] { "Status", "NextDecisionAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatrixPilotEvents");

            migrationBuilder.DropTable(
                name: "MatrixPilotMandates");

            migrationBuilder.DropTable(
                name: "MatrixPilotSteps");

            migrationBuilder.DropTable(
                name: "MatrixPilotCycles");

            migrationBuilder.DropTable(
                name: "MatrixPilots");
        }
    }
}
