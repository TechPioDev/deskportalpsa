using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SlaCannedTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstRespondedAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstResponseDueAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SlaPlanId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSlaPlanId",
                table: "boards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SlaPlanId",
                table: "board_topics",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "canned_responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canned_responses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sla_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResolveWithinHours = table.Column<int>(type: "integer", nullable: false),
                    FirstResponseWithinHours = table.Column<int>(type: "integer", nullable: true),
                    BusinessHoursOnly = table.Column<bool>(type: "boolean", nullable: false),
                    WorkdayStartHour = table.Column<int>(type: "integer", nullable: false),
                    WorkdayEndHour = table.Column<int>(type: "integer", nullable: false),
                    WorkingDays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DoneByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_tasks_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_canned_responses_MspOrganizationId_BoardId_Name",
                table: "canned_responses",
                columns: new[] { "MspOrganizationId", "BoardId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sla_plans_MspOrganizationId_Name",
                table: "sla_plans",
                columns: new[] { "MspOrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_tasks_TicketId_SortOrder",
                table: "ticket_tasks",
                columns: new[] { "TicketId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "canned_responses");

            migrationBuilder.DropTable(
                name: "sla_plans");

            migrationBuilder.DropTable(
                name: "ticket_tasks");

            migrationBuilder.DropColumn(
                name: "FirstRespondedAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "FirstResponseDueAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "SlaPlanId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DefaultSlaPlanId",
                table: "boards");

            migrationBuilder.DropColumn(
                name: "SlaPlanId",
                table: "board_topics");
        }
    }
}
