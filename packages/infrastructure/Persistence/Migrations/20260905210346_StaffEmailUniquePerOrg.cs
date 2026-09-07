using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes a staff email unique within its organization, in the database rather than only in code.
    ///
    /// Creation and update already refuse a duplicate, but they do it by querying first and then
    /// inserting — two requests can pass that check before either writes. Client users have had a
    /// real unique index on (ClientCompanyId, Email) all along; staff never did.
    ///
    /// It matters more than a duplicate row usually would: email is how a Keycloak identity is bound
    /// to a portal user, so two rows sharing one address make that resolution ambiguous.
    ///
    /// Written as raw SQL because the index is over lower("Email"), which EF cannot express. A plain
    /// unique index would be WEAKER than the application rule it is meant to back — the app compares
    /// case-insensitively, so a plain index would accept "A@b.test" beside "a@b.test" while the app
    /// refused it, and the database would then hold a pair the application believes impossible.
    ///
    /// If an environment already holds duplicates this migration fails, and Postgres names the
    /// offending key. That is the intended behaviour — silently dropping one of two staff accounts
    /// would be worse. Find them first with:
    ///   select "MspOrganizationId", lower("Email"), count(*) from app_users
    ///   group by 1, 2 having count(*) > 1;
    /// </summary>
    public partial class StaffEmailUniquePerOrg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_app_users_MspOrganizationId_EmailLower"
                ON app_users ("MspOrganizationId", lower("Email"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_app_users_MspOrganizationId_EmailLower";""");
        }
    }
}
