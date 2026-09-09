using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketPortalAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedAppUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_AssignedAppUserId",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "AssignedAppUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_AssignedAppUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AssignedAppUserId",
                table: "tickets");
        }
    }
}
