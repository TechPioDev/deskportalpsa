using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApproverName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApproverEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Request = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RequestedByAppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Channel = table.Column<int>(type: "integer", nullable: true),
                    DecidedByClientUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_approvals_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_approvals_ClientCompanyId_State",
                table: "ticket_approvals",
                columns: new[] { "ClientCompanyId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_approvals_TicketId",
                table: "ticket_approvals",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_approvals");
        }
    }
}
