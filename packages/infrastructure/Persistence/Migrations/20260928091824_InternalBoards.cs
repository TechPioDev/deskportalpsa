using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InternalBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PsaConnectionId",
                table: "tickets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "Provider",
                table: "tickets",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientCompanyId",
                table: "tickets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedByUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BoardId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "boards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Key = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ClientVisible = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    NextNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_boards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_assignments_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "board_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_board_members_boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_BoardId",
                table: "tickets",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_BoardId_PortalStatus",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "BoardId", "PortalStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_Number",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "Number" },
                unique: true,
                filter: "\"Number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_board_members_BoardId_AppUserId",
                table: "board_members",
                columns: new[] { "BoardId", "AppUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_boards_MspOrganizationId_Key",
                table: "boards",
                columns: new[] { "MspOrganizationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_assignments_TicketId_CreatedAt",
                table: "ticket_assignments",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_tickets_boards_BoardId",
                table: "tickets",
                column: "BoardId",
                principalTable: "boards",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tickets_boards_BoardId",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "board_members");

            migrationBuilder.DropTable(
                name: "ticket_assignments");

            migrationBuilder.DropTable(
                name: "boards");

            migrationBuilder.DropIndex(
                name: "IX_tickets_BoardId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_BoardId_PortalStatus",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_Number",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AssignedByUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "tickets");

            migrationBuilder.AlterColumn<Guid>(
                name: "PsaConnectionId",
                table: "tickets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Provider",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientCompanyId",
                table: "tickets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
