using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContaminantMigration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_Role", "\"Role\" IN ('User','Admin')");
                });

            migrationBuilder.CreateTable(
                name: "SimulationTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    SolverVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedSteps = table.Column<int>(type: "integer", nullable: false),
                    TotalSteps = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationTasks", x => x.Id);
                    table.CheckConstraint("CK_Tasks_Progress", "\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100");
                    table.CheckConstraint("CK_Tasks_Status", "\"Status\" IN ('Queued','Running','Cancelling','Completed','Cancelled','Failed','TimedOut')");
                    table.CheckConstraint("CK_Tasks_Steps", "\"TotalSteps\" > 0 AND \"CompletedSteps\" >= 0 AND \"CompletedSteps\" <= \"TotalSteps\"");
                    table.ForeignKey(
                        name: "FK_SimulationTasks_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SimulationResults",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodesX = table.Column<int>(type: "integer", nullable: false),
                    NodesY = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationResults", x => x.TaskId);
                    table.ForeignKey(
                        name: "FK_SimulationResults_SimulationTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "SimulationTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SimulationFrames",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: false),
                    TimeSeconds = table.Column<double>(type: "double precision", nullable: false),
                    MinimumConcentration = table.Column<float>(type: "real", nullable: false),
                    MaximumConcentration = table.Column<float>(type: "real", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationFrames", x => new { x.TaskId, x.Index });
                    table.ForeignKey(
                        name: "FK_SimulationFrames_SimulationResults_TaskId",
                        column: x => x.TaskId,
                        principalTable: "SimulationResults",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SimulationTasks_OwnerId_CreatedAt_Id",
                table: "SimulationTasks",
                columns: new[] { "OwnerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SimulationTasks_Status_CreatedAt",
                table: "SimulationTasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimulationFrames");

            migrationBuilder.DropTable(
                name: "SimulationResults");

            migrationBuilder.DropTable(
                name: "SimulationTasks");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
