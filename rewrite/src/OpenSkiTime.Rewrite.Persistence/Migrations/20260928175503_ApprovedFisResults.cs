using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovedFisResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovedResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompetitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecondListId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    XmlFileName = table.Column<string>(type: "TEXT", nullable: false),
                    Xml = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CalculatedPenalty = table.Column<decimal>(type: "TEXT", nullable: false),
                    AppliedPenalty = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovedResults_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovedResults_StartLists_FirstListId",
                        column: x => x.FirstListId,
                        principalTable: "StartLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedResults_CompetitionId_Revision",
                table: "ApprovedResults",
                columns: new[] { "CompetitionId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedResults_FirstListId",
                table: "ApprovedResults",
                column: "FirstListId");
            migrationBuilder.Sql("CREATE TRIGGER ApprovedResults_NoUpdate BEFORE UPDATE ON ApprovedResults BEGIN SELECT RAISE(ABORT, 'Approved results are immutable'); END;");
            migrationBuilder.Sql("CREATE TRIGGER ApprovedResults_NoDelete BEFORE DELETE ON ApprovedResults BEGIN SELECT RAISE(ABORT, 'Approved results are immutable'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ApprovedResults_NoUpdate;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS ApprovedResults_NoDelete;");
            migrationBuilder.DropTable(
                name: "ApprovedResults");
        }
    }
}
