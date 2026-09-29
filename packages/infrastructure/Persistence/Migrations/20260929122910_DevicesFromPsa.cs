using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicesFromPsa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceExternalId",
                table: "tickets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "devices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncedAt",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PsaConnectionId",
                table: "devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WarrantyExpiresAt",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_DeviceId",
                table: "tickets",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_PsaConnectionId_ExternalId",
                table: "devices",
                columns: new[] { "PsaConnectionId", "ExternalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_DeviceId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_devices_PsaConnectionId_ExternalId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "DeviceExternalId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "LastSyncedAt",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "PsaConnectionId",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "WarrantyExpiresAt",
                table: "devices");
        }
    }
}
