using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PsaSyncEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_psa_identities_PsaConnectionId",
                table: "user_psa_identities");

            migrationBuilder.CreateTable(
                name: "sync_cursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Entity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Watermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Continuation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContinuationSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ContinuationStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ContinuationFull = table.Column<bool>(type: "boolean", nullable: false),
                    ContinuationPages = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_cursors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_cursors_psa_connections_PsaConnectionId",
                        column: x => x.PsaConnectionId,
                        principalTable: "psa_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_failures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Entity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FirstFailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastFailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_failures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_failures_psa_connections_PsaConnectionId",
                        column: x => x.PsaConnectionId,
                        principalTable: "psa_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PsaConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Fetched = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Pages = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<int>(type: "integer", nullable: false),
                    NotesRemoved = table.Column<int>(type: "integer", nullable: false),
                    Attachments = table.Column<int>(type: "integer", nullable: false),
                    AttachmentsRemoved = table.Column<int>(type: "integer", nullable: false),
                    FailedRecords = table.Column<int>(type: "integer", nullable: false),
                    Retried = table.Column<int>(type: "integer", nullable: false),
                    Recovered = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Notice = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_runs_psa_connections_PsaConnectionId",
                        column: x => x.PsaConnectionId,
                        principalTable: "psa_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_psa_identities_PsaConnectionId_ExternalTechnicianId",
                table: "user_psa_identities",
                columns: new[] { "PsaConnectionId", "ExternalTechnicianId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_cursors_PsaConnectionId_Entity",
                table: "sync_cursors",
                columns: new[] { "PsaConnectionId", "Entity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_failures_PsaConnectionId_Status_NextAttemptAt",
                table: "sync_failures",
                columns: new[] { "PsaConnectionId", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_failures_one_open",
                table: "sync_failures",
                columns: new[] { "PsaConnectionId", "Entity", "ExternalId", "Operation" },
                unique: true,
                filter: "\"Status\" IN (0, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_PsaConnectionId_StartedAt",
                table: "sync_runs",
                columns: new[] { "PsaConnectionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_one_running",
                table: "sync_runs",
                column: "PsaConnectionId",
                unique: true,
                filter: "\"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_cursors");

            migrationBuilder.DropTable(
                name: "sync_failures");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropIndex(
                name: "IX_user_psa_identities_PsaConnectionId_ExternalTechnicianId",
                table: "user_psa_identities");

            migrationBuilder.CreateIndex(
                name: "IX_user_psa_identities_PsaConnectionId",
                table: "user_psa_identities",
                column: "PsaConnectionId");
        }
    }
}
