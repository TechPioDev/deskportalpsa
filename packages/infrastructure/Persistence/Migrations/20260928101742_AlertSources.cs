using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AlertSourceId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAlertId",
                table: "tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alert_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Vendor = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyHint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CloseOnClear = table.Column<bool>(type: "boolean", nullable: false),
                    LastReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_sources_boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_AlertSourceId_SourceAlertId",
                table: "tickets",
                columns: new[] { "AlertSourceId", "SourceAlertId" },
                unique: true,
                filter: "\"SourceAlertId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_alert_sources_BoardId",
                table: "alert_sources",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_sources_KeyHash",
                table: "alert_sources",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_sources");

            migrationBuilder.DropIndex(
                name: "IX_tickets_AlertSourceId_SourceAlertId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AlertSourceId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "SourceAlertId",
                table: "tickets");
        }
    }
}
