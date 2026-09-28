using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenSkiTime.Rewrite.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimingCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceTimingVersion",
                table: "StartLists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TimingAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Operator = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: false),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReversesId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingAudit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimingAudit_StartLists_ListId",
                        column: x => x.ListId,
                        principalTable: "StartLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TimingCaptures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CleanStop = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimingCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimingCaptures_StartLists_ListId",
                        column: x => x.ListId,
                        principalTable: "StartLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RawTimingPackets",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Stream = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawTimingPackets", x => new { x.SessionId, x.Sequence });
                    table.CheckConstraint("CK_Raw_Sequence", "Sequence > 0");
                    table.ForeignKey(
                        name: "FK_RawTimingPackets_TimingCaptures_SessionId",
                        column: x => x.SessionId,
                        principalTable: "TimingCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimingAudit_ListId_Id",
                table: "TimingAudit",
                columns: new[] { "ListId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TimingCaptures_ListId",
                table: "TimingCaptures",
                column: "ListId");

            // Original transport bytes and correction history are append-only, including direct SQL access.
            foreach (var table in new[] { "RawTimingPackets", "TimingAudit" })
            {
                migrationBuilder.Sql($"CREATE TRIGGER {table}_NoUpdate BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT, 'Timing history is immutable'); END;");
                migrationBuilder.Sql($"CREATE TRIGGER {table}_NoDelete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT, 'Timing history is immutable'); END;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawTimingPackets");

            migrationBuilder.DropTable(
                name: "TimingAudit");

            migrationBuilder.DropTable(
                name: "TimingCaptures");

            migrationBuilder.DropColumn(
                name: "SourceTimingVersion",
                table: "StartLists");
        }
    }
}
