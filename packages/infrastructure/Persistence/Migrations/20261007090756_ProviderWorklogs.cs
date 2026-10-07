using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderWorklogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ticket_time_entries_PsaConnectionId_ExternalEntryId",
                table: "ticket_time_entries",
                columns: new[] { "PsaConnectionId", "ExternalEntryId" },
                unique: true,
                filter: "\"PsaConnectionId\" IS NOT NULL AND \"ExternalEntryId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ticket_time_entries_PsaConnectionId_ExternalEntryId",
                table: "ticket_time_entries");
        }
    }
}
