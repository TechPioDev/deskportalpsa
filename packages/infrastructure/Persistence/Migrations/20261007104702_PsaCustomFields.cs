using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PsaCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomFieldsJson",
                table: "tickets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "psa_custom_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Import = table.Column<bool>(type: "boolean", nullable: false),
                    PortalLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_psa_custom_fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_psa_custom_fields_psa_connections_PsaConnectionId",
                        column: x => x.PsaConnectionId,
                        principalTable: "psa_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_psa_custom_fields_PsaConnectionId_ExternalKey",
                table: "psa_custom_fields",
                columns: new[] { "PsaConnectionId", "ExternalKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "psa_custom_fields");

            migrationBuilder.DropColumn(
                name: "CustomFieldsJson",
                table: "tickets");
        }
    }
}
