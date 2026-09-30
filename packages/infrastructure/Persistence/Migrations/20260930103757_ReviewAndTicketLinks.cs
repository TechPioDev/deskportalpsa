using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewAndTicketLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewSendBacks",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReviewState",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireReview",
                table: "boards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireReview",
                table: "board_topics",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ticket_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromTicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToTicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_links_tickets_FromTicketId",
                        column: x => x.FromTicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_links_tickets_ToTicketId",
                        column: x => x.ToTicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_FromTicketId_ToTicketId_Kind",
                table: "ticket_links",
                columns: new[] { "FromTicketId", "ToTicketId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_ToTicketId",
                table: "ticket_links",
                column: "ToTicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_links");

            migrationBuilder.DropColumn(
                name: "ReviewSendBacks",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "RequireReview",
                table: "boards");

            migrationBuilder.DropColumn(
                name: "RequireReview",
                table: "board_topics");
        }
    }
}
