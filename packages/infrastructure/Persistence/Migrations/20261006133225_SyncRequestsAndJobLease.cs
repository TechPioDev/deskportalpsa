using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncRequestsAndJobLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SyncRequestedAt",
                table: "psa_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRequestedBy",
                table: "psa_connections",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SyncRequestedFull",
                table: "psa_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "background_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "background_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncRequestedAt",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "SyncRequestedBy",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "SyncRequestedFull",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "background_jobs");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "background_jobs");
        }
    }
}
