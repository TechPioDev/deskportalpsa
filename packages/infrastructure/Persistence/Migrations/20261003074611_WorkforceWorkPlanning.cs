using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkforceWorkPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_planning",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequiredMinutes = table.Column<int>(type: "integer", nullable: true),
                    EarliestStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LatestEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Splittable = table.Column<bool>(type: "boolean", nullable: false),
                    RequiredSkillId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_planning", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_planning_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_planning_MspOrganizationId",
                table: "work_planning",
                column: "MspOrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_work_planning_TicketId",
                table: "work_planning",
                column: "TicketId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_planning");
        }
    }
}
