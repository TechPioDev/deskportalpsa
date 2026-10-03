using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkforceWorkSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkSessionId",
                table: "ticket_time_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "work_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PauseReason = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActiveSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_sessions_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_session_segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Seconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_session_segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_session_segments_work_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "work_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_time_entries_WorkSessionId",
                table: "ticket_time_entries",
                column: "WorkSessionId",
                unique: true,
                filter: "\"WorkSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_work_session_segments_SessionId_StartedAt",
                table: "work_session_segments",
                columns: new[] { "SessionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_sessions_AppUserId_Status",
                table: "work_sessions",
                columns: new[] { "AppUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_work_sessions_MspOrganizationId_AppUserId_StartedAt",
                table: "work_sessions",
                columns: new[] { "MspOrganizationId", "AppUserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_sessions_TicketId",
                table: "work_sessions",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_work_sessions_one_active",
                table: "work_sessions",
                column: "AppUserId",
                unique: true,
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_session_segments");

            migrationBuilder.DropTable(
                name: "work_sessions");

            migrationBuilder.DropIndex(
                name: "IX_ticket_time_entries_WorkSessionId",
                table: "ticket_time_entries");

            migrationBuilder.DropColumn(
                name: "WorkSessionId",
                table: "ticket_time_entries");
        }
    }
}
