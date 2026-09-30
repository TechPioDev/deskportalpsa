using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_CreatedAt",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_CreatedByUserId",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_ResolvedAt",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_SlaDueAt",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "SlaDueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_CreatedAt",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_CreatedByUserId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_ResolvedAt",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_SlaDueAt",
                table: "tickets");
        }
    }
}
