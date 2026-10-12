using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketFollowerNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AddedNotifiedAt",
                table: "ticket_followers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotifiedThrough",
                table: "ticket_followers",
                type: "timestamp with time zone",
                nullable: true);

            // People who already follow a ticket are not told so on the day this arrives, and are
            // not sent the ticket's past: for them the count starts now.
            migrationBuilder.Sql("""UPDATE ticket_followers SET "AddedNotifiedAt" = now(), "NotifiedThrough" = now();""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedNotifiedAt",
                table: "ticket_followers");

            migrationBuilder.DropColumn(
                name: "NotifiedThrough",
                table: "ticket_followers");
        }
    }
}
