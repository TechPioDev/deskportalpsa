using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SearchViewsFollowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedTeamId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "saved_ticket_views",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Shared = table.Column<bool>(type: "boolean", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Search = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Company = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Queue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConnectionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PersonKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Openness = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MineOnly = table.Column<bool>(type: "boolean", nullable: false),
                    FollowingOnly = table.Column<bool>(type: "boolean", nullable: false),
                    UnassignedOnly = table.Column<bool>(type: "boolean", nullable: false),
                    OverdueOnly = table.Column<bool>(type: "boolean", nullable: false),
                    RaisedWithinDays = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_ticket_views", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_followers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_followers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_followers_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_AssignedTeamId",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "AssignedTeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_saved_ticket_views_MspOrganizationId_OwnerUserId_BoardId_Na~",
                table: "saved_ticket_views",
                columns: new[] { "MspOrganizationId", "OwnerUserId", "BoardId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_followers_MspOrganizationId_AppUserId",
                table: "ticket_followers",
                columns: new[] { "MspOrganizationId", "AppUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_followers_TicketId_AppUserId",
                table: "ticket_followers",
                columns: new[] { "TicketId", "AppUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_ticket_views");

            migrationBuilder.DropTable(
                name: "ticket_followers");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_AssignedTeamId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AssignedTeamId",
                table: "tickets");
        }
    }
}
