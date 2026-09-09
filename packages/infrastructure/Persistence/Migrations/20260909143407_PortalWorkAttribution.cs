using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortalWorkAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppUserId",
                table: "ticket_time_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorAppUserId",
                table: "activity_daily_facts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_time_entries_MspOrganizationId_AppUserId_EntryDate",
                table: "ticket_time_entries",
                columns: new[] { "MspOrganizationId", "AppUserId", "EntryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_time_entries_MspOrganizationId_AppUserId_EntryDate",
                table: "ticket_time_entries");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "ticket_time_entries");

            migrationBuilder.DropColumn(
                name: "ActorAppUserId",
                table: "activity_daily_facts");
        }
    }
}
