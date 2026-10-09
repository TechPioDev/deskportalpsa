using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketDueDateExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueDateExtendedAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DueDateExtendedByName",
                table: "tickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DueDateExtendedByUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DueDateExtensionReason",
                table: "tickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DueDateExtensions",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OriginalSlaDueAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueDateExtendedAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DueDateExtendedByName",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DueDateExtendedByUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DueDateExtensionReason",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DueDateExtensions",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "OriginalSlaDueAt",
                table: "tickets");
        }
    }
}
