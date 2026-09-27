using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StartLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Gender = table.Column<string>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.CheckConstraint("CK_Run_Number", "Number BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "FK_Runs_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StartLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Operator = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StartLists_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StartListEntries",
                columns: table => new
                {
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Bib = table.Column<int>(type: "INTEGER", nullable: false),
                    CompetitorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StartListEntries", x => new { x.ListId, x.Position });
                    table.CheckConstraint("CK_Start_Bib", "Bib BETWEEN 1 AND 99999");
                    table.CheckConstraint("CK_Start_Position", "Position > 0");
                    table.ForeignKey(
                        name: "FK_StartListEntries_StartLists_ListId",
                        column: x => x.ListId,
                        principalTable: "StartLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CompetitionId_Gender_Number",
                table: "Runs",
                columns: new[] { "CompetitionId", "Gender", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StartListEntries_ListId_Bib",
                table: "StartListEntries",
                columns: new[] { "ListId", "Bib" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StartListEntries_ListId_CompetitorId",
                table: "StartListEntries",
                columns: new[] { "ListId", "CompetitorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StartLists_RunId_Revision",
                table: "StartLists",
                columns: new[] { "RunId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StartListEntries");

            migrationBuilder.DropTable(
                name: "StartLists");

            migrationBuilder.DropTable(
                name: "Runs");
        }
    }
}
