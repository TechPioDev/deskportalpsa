using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlertSourceClientPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientCompanyId",
                table: "alert_sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_sources_ClientCompanyId",
                table: "alert_sources",
                column: "ClientCompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_sources_client_companies_ClientCompanyId",
                table: "alert_sources",
                column: "ClientCompanyId",
                principalTable: "client_companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_alert_sources_client_companies_ClientCompanyId",
                table: "alert_sources");

            migrationBuilder.DropIndex(
                name: "IX_alert_sources_ClientCompanyId",
                table: "alert_sources");

            migrationBuilder.DropColumn(
                name: "ClientCompanyId",
                table: "alert_sources");
        }
    }
}
