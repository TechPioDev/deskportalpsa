using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketSatisfaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_satisfaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TechnicianAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TechnicianExternalId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TechnicianName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_satisfaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_satisfaction_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_satisfaction_MspOrganizationId_RatedAt",
                table: "ticket_satisfaction",
                columns: new[] { "MspOrganizationId", "RatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_satisfaction_TicketId",
                table: "ticket_satisfaction",
                column: "TicketId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_satisfaction");
        }
    }
}
