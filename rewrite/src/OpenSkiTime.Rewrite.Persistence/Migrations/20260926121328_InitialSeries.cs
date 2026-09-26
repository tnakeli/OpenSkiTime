using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    SingleRow = table.Column<int>(type: "INTEGER", nullable: false),
                    FormatId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Organizer = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Nation = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Season = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.SingleRow);
                    table.CheckConstraint("CK_Series_Format", "FormatId = 'OpenSkiTime.New/1'");
                    table.CheckConstraint("CK_Series_OneRow", "SingleRow = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_Id",
                table: "Series",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Series");
        }
    }
}
