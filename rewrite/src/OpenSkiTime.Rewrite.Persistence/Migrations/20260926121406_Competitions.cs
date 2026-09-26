using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Competitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Series_Id",
                table: "Series",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Competitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    ShortLabel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ShortLabelKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Discipline = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RaceType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RunCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IntermediateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FisCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LocalRaceCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CourseName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    StartAltitudeMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    FinishAltitudeMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    VerticalDropMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    HomologationNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competitions", x => x.Id);
                    table.CheckConstraint("CK_Competition_Intermediates", "IntermediateCount BETWEEN 0 AND 20");
                    table.CheckConstraint("CK_Competition_Runs", "RunCount BETWEEN 1 AND 9");
                    table.ForeignKey(
                        name: "FK_Competitions_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Competitions_SeriesId_ShortLabelKey",
                table: "Competitions",
                columns: new[] { "SeriesId", "ShortLabelKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Competitions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Series_Id",
                table: "Series");
        }
    }
}
