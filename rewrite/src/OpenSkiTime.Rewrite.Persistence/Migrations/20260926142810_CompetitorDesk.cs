using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompetitorDesk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Competitions_SeriesId_Id",
                table: "Competitions",
                columns: new[] { "SeriesId", "Id" });

            migrationBuilder.CreateTable(
                name: "CategoryRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LabelKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BirthYearMin = table.Column<int>(type: "INTEGER", nullable: false),
                    BirthYearMax = table.Column<int>(type: "INTEGER", nullable: false),
                    Gender = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryRules", x => x.Id);
                    table.CheckConstraint("CK_Category_Years", "BirthYearMin >= 1850 AND BirthYearMin <= BirthYearMax");
                    table.ForeignKey(
                        name: "FK_CategoryRules_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Competitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Surname = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BirthYear = table.Column<int>(type: "INTEGER", nullable: true),
                    FederationCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FederationCodeKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Nation = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    Club = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Gender = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competitors", x => x.Id);
                    table.UniqueConstraint("AK_Competitors_SeriesId_Id", x => new { x.SeriesId, x.Id });
                    table.CheckConstraint("CK_Competitor_BirthYear", "BirthYear IS NULL OR BirthYear >= 1850");
                    table.ForeignKey(
                        name: "FK_Competitors_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participations",
                columns: table => new
                {
                    CompetitorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Participates = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImportedBib = table.Column<int>(type: "INTEGER", nullable: true),
                    StartOrder = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participations", x => new { x.CompetitorId, x.CompetitionId });
                    table.CheckConstraint("CK_Participation_Bib", "ImportedBib IS NULL OR ImportedBib BETWEEN 1 AND 99999");
                    table.ForeignKey(
                        name: "FK_Participations_Competitions_SeriesId_CompetitionId",
                        columns: x => new { x.SeriesId, x.CompetitionId },
                        principalTable: "Competitions",
                        principalColumns: new[] { "SeriesId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Participations_Competitors_SeriesId_CompetitorId",
                        columns: x => new { x.SeriesId, x.CompetitorId },
                        principalTable: "Competitors",
                        principalColumns: new[] { "SeriesId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryRules_SeriesId_LabelKey",
                table: "CategoryRules",
                columns: new[] { "SeriesId", "LabelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Competitors_SeriesId_FederationCodeKey",
                table: "Competitors",
                columns: new[] { "SeriesId", "FederationCodeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participations_SeriesId_CompetitionId_ImportedBib",
                table: "Participations",
                columns: new[] { "SeriesId", "CompetitionId", "ImportedBib" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participations_SeriesId_CompetitorId",
                table: "Participations",
                columns: new[] { "SeriesId", "CompetitorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryRules");

            migrationBuilder.DropTable(
                name: "Participations");

            migrationBuilder.DropTable(
                name: "Competitors");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Competitions_SeriesId_Id",
                table: "Competitions");
        }
    }
}
