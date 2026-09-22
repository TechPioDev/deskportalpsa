using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GraphEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GraphClientId",
                table: "organization_email_settings",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GraphTenantId",
                table: "organization_email_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "organization_email_settings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Smtp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraphClientId",
                table: "organization_email_settings");

            migrationBuilder.DropColumn(
                name: "GraphTenantId",
                table: "organization_email_settings");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "organization_email_settings");
        }
    }
}
