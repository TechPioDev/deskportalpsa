using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BoardTopicsAndFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BoardTopicId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "tickets",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "board_topics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DefaultDepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultPriority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DefaultAssigneeUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueInHours = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_topics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_board_topics_boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_DepartmentId",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_board_topics_BoardId_Name",
                table: "board_topics",
                columns: new[] { "BoardId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "board_topics");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_DepartmentId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "BoardTopicId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "tickets");
        }
    }
}
