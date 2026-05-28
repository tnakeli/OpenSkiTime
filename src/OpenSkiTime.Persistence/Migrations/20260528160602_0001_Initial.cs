using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0001_Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Organizer = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Nation = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Season = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Competitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventSeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ShortLabel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Discipline = table.Column<int>(type: "INTEGER", nullable: false),
                    RaceType = table.Column<int>(type: "INTEGER", nullable: false),
                    FisCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LocalRaceCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Gender = table.Column<int>(type: "INTEGER", nullable: true),
                    CourseName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartAltitudeMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    FinishAltitudeMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    VerticalDropMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    HomologationNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NumberOfRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    NumberOfIntermediateTimes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Competitions_EventSeries_EventSeriesId",
                        column: x => x.EventSeriesId,
                        principalTable: "EventSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Competitions_EventSeriesId_Date",
                table: "Competitions",
                columns: new[] { "EventSeriesId", "Date" });

            migrationBuilder.CreateIndex(
                name: "UQ_Competitions_EventSeriesId_ShortLabel",
                table: "Competitions",
                columns: new[] { "EventSeriesId", "ShortLabel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Competitions");

            migrationBuilder.DropTable(
                name: "EventSeries");
        }
    }
}
