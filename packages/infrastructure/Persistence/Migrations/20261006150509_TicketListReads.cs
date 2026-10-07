using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketListReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PsaConnectionId",
                table: "ticket_time_entries",
                type: "uuid",
                nullable: true);

            // Every time entry already here is given the PSA account of its ticket. New ones are
            // given it as they are saved.
            migrationBuilder.Sql(Desk.Infrastructure.Persistence.Configurations.TicketIndexes.GiveTimeEntriesTheirAccount);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_PsaConnectionId_AssignedTechnicia~",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "PsaConnectionId", "AssignedTechnicianExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_MspOrganizationId_ResolvedByAppUserId",
                table: "tickets",
                columns: new[] { "MspOrganizationId", "ResolvedByAppUserId" },
                filter: "\"ResolvedByAppUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_open",
                table: "tickets",
                column: "MspOrganizationId",
                filter: "upper(\"PortalStatus\") NOT LIKE '%RESOLV%' AND upper(\"PortalStatus\") NOT LIKE '%CLOSED%'")
                .Annotation("Npgsql:IndexInclude", new[] { "Origin", "BoardId", "AssignedAppUserId", "CreatedByUserId", "PortalStatus", "PortalPriority", "PsaConnectionId", "SlaPausedAt", "SlaDueAt", "AssignedTechnicianExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_time_entries_MspOrganizationId_PsaConnectionId_Techn~",
                table: "ticket_time_entries",
                columns: new[] { "MspOrganizationId", "PsaConnectionId", "TechnicianExternalId" },
                filter: "\"AppUserId\" IS NULL");

            // The search looks for a few letters anywhere in a ticket's number, subject or requester.
            // That is "contains", which an ordinary index cannot answer; an index of the three-letter
            // pieces of each can. The expressions are the ones the search sends (lower(...) LIKE),
            // and it is only used while they stay the same. pg_trgm has shipped with PostgreSQL since
            // long before 17 and may be added by the database's owner.
            //
            // IF NOT EXISTS, so that on a table too large to hold still for the build the index can be
            // made beforehand with CREATE INDEX CONCURRENTLY under this name, and this then does nothing.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_tickets_search" ON tickets USING gin (
                    lower("Title") gin_trgm_ops, lower("RequesterName") gin_trgm_ops,
                    lower("ExternalTicketId") gin_trgm_ops, lower("Number") gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The extension is left: something else may have come to use it.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_tickets_search";""");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_PsaConnectionId_AssignedTechnicia~",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_MspOrganizationId_ResolvedByAppUserId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_open",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_ticket_time_entries_MspOrganizationId_PsaConnectionId_Techn~",
                table: "ticket_time_entries");

            migrationBuilder.DropColumn(
                name: "PsaConnectionId",
                table: "ticket_time_entries");
        }
    }
}
