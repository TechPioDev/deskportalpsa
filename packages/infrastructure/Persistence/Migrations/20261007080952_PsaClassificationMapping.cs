using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PsaClassificationMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PortalWorkType",
                table: "tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "classification_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IssueType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubIssueType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Subcategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classification_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_classification_mappings_psa_connections_PsaConnectionId",
                        column: x => x.PsaConnectionId,
                        principalTable: "psa_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_classification_mappings_PsaConnectionId_TicketType_IssueTyp~",
                table: "classification_mappings",
                columns: new[] { "PsaConnectionId", "TicketType", "IssueType", "SubIssueType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "classification_mappings");

            migrationBuilder.DropColumn(
                name: "PortalWorkType",
                table: "tickets");
        }
    }
}
