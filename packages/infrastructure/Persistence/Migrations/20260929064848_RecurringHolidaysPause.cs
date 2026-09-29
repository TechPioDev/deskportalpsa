using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecurringHolidaysPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlaPausedAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PauseWhileWaiting",
                table: "sla_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipHolidays",
                table: "sla_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "desk_holidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_desk_holidays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recurring_tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BoardTopicId = table.Column<Guid>(type: "uuid", nullable: true),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientCompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Checklist = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: true),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: false),
                    SkipIfOpen = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recurring_tickets_boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_desk_holidays_MspOrganizationId_Date",
                table: "desk_holidays",
                columns: new[] { "MspOrganizationId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_tickets_BoardId",
                table: "recurring_tickets",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_tickets_IsActive_NextRunAt",
                table: "recurring_tickets",
                columns: new[] { "IsActive", "NextRunAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "desk_holidays");

            migrationBuilder.DropTable(
                name: "recurring_tickets");

            migrationBuilder.DropColumn(
                name: "SlaPausedAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "PauseWhileWaiting",
                table: "sla_plans");

            migrationBuilder.DropColumn(
                name: "SkipHolidays",
                table: "sla_plans");
        }
    }
}
