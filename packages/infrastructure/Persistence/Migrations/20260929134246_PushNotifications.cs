using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PushPrivateKeyRef",
                table: "msp_organizations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PushPublicKey",
                table: "msp_organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "push_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Delivered = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "push_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Assigned = table.Column<bool>(type: "boolean", nullable: false),
                    ClientReplied = table.Column<bool>(type: "boolean", nullable: false),
                    SlaAtRisk = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_preferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "push_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LastDeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_subscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "push_ticket_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    HolderAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastClientNoteAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SlaWarnedDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_ticket_states", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_push_notifications_AppUserId_CreatedAt",
                table: "push_notifications",
                columns: new[] { "AppUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_push_notifications_SentAt_CreatedAt",
                table: "push_notifications",
                columns: new[] { "SentAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_push_preferences_AppUserId",
                table: "push_preferences",
                column: "AppUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_AppUserId",
                table: "push_subscriptions",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_push_subscriptions_Endpoint",
                table: "push_subscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_push_ticket_states_TicketId",
                table: "push_ticket_states",
                column: "TicketId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_notifications");

            migrationBuilder.DropTable(
                name: "push_preferences");

            migrationBuilder.DropTable(
                name: "push_subscriptions");

            migrationBuilder.DropTable(
                name: "push_ticket_states");

            migrationBuilder.DropColumn(
                name: "PushPrivateKeyRef",
                table: "msp_organizations");

            migrationBuilder.DropColumn(
                name: "PushPublicKey",
                table: "msp_organizations");
        }
    }
}
