using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketAssignmentNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotifiedAt",
                table: "ticket_assignments",
                type: "timestamp with time zone",
                nullable: true);

            // Every assignment made before today counts as dealt with. Without this the first turn
            // would mail everybody about every ticket they were ever given.
            migrationBuilder.Sql("""UPDATE ticket_assignments SET "NotifiedAt" = now();""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifiedAt",
                table: "ticket_assignments");
        }
    }
}
