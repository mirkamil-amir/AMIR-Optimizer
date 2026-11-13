using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioOptimizer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MatrixPilotFitAndShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComparisonJson",
                table: "MatrixPilotCycles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FitRevisions",
                table: "MatrixPilotCycles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InitialDecisionJson",
                table: "MatrixPilotCycles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowJson",
                table: "MatrixPilotCycles",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComparisonJson",
                table: "MatrixPilotCycles");

            migrationBuilder.DropColumn(
                name: "FitRevisions",
                table: "MatrixPilotCycles");

            migrationBuilder.DropColumn(
                name: "InitialDecisionJson",
                table: "MatrixPilotCycles");

            migrationBuilder.DropColumn(
                name: "ShadowJson",
                table: "MatrixPilotCycles");
        }
    }
}
