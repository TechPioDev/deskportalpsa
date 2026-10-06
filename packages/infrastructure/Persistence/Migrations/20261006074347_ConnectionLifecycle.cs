using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConnectionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountKeyHash",
                table: "psa_connections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "psa_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InSetup",
                table: "psa_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorKind",
                table: "psa_connections",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SyncPausedAt",
                table: "psa_connections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccountKeyHash",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "InSetup",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "LastErrorKind",
                table: "psa_connections");

            migrationBuilder.DropColumn(
                name: "SyncPausedAt",
                table: "psa_connections");
        }
    }
}
