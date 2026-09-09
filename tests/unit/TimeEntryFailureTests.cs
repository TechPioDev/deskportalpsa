using Desk.Api.Controllers;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Tenancy;
using Desk.Domain.Tickets;
using Desk.PsaCore.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// What a rejected time entry tells the technician afterwards.
///
/// The live failure this pins: an Autotask entry failed for a missing time-entry resource, the admin
/// configured one, retried — and Autotask rejected it for a DIFFERENT reason (HTTP 500). Because a
/// provider rejection arrived as an EXCEPTION rather than a failed result, the row kept the message
/// it failed with the FIRST time, so the screen still said "set a default time-entry resource" for a
/// connection that already had one. The admin was sent to fix something that was not broken.
/// </summary>
public class TimeEntryFailureTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed class FakeResolver(IServiceManagementConnector c) : Desk.Application.Connectors.IConnectorResolver
    {
        public Task<IServiceManagementConnector> ResolveAsync(Guid id, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed class TestUser(Guid userId) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? Subject => "sub";
        public string? Email => "tech@msp.test";
        public string? DisplayName => "Tech";
        public Guid? OrganizationId => Org;
        public Guid? UserId => userId;
        public string? TechnicianExternalId => null;
        public IReadOnlySet<string> Permissions => new HashSet<string> { Desk.Domain.Authorization.Permissions.TicketsLogTime };
        public bool HasPermission(string permissionKey) => Permissions.Contains(permissionKey);
    }

    [Fact]
    public async Task A_logged_hour_records_which_PORTAL_user_logged_it()
    {
        // Attribution is stamped on the row at creation, and this is the test that fails if that
        // stamp is ever dropped. Without it every hour the team logs is unattributable: the PSA
        // side falls back to the connection's default resource, so all forty technicians would
        // share one identity and "hours per technician" would be a single row.
        //
        // Driven through the FAILING push on purpose. The row is written before the provider call,
        // so a rejected entry still has to carry who logged it - that is the entry someone will
        // retry days later, and the hour belongs to whoever did the work, not whoever retried it.
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" };
        h.Db.ClientCompanies.Add(company);
        var ticket = new Ticket
        {
            MspOrganizationId = Org, ClientCompanyId = company.Id, PsaConnectionId = Conn,
            Title = "t", RequesterName = "r", RequesterEmail = "r@a.test", ExternalTicketId = "7814",
        };
        h.Db.Tickets.Add(ticket);
        await h.Db.SaveChangesAsync();

        var basit = Guid.NewGuid();
        var connector = new StubConnector
        {
            TimeEntryFailure = new ConnectorException(ConnectorFailureKind.InvalidRequest, "rejected"),
        };
        var controller = new TicketTimeController(
            h.Db, new FakeResolver(connector), null!, new NoopTicketScopeQuery(), new TestUser(basit));

        var act = async () => await controller.LogTime(ticket.Id,
            new TicketTimeController.LogTimeRequest(1.5m, "Billable", "worked on it", null, null, null), default);
        await act.Should().ThrowAsync<ValidationFailedException>();

        var stored = await h.Db.TicketTimeEntries.AsNoTracking().SingleAsync();
        stored.AppUserId.Should().Be(basit);
        stored.Hours.Should().Be(1.5m);
        stored.TechnicianExternalId.Should().BeNull(
            "this technician has no PSA identity - which is exactly why the portal id has to be recorded");
    }

    [Fact]
    public async Task A_provider_rejection_replaces_the_stale_reason_with_what_the_provider_actually_said()
    {
        var h = AdminHarness.Create(Org);
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = Conn, MspOrganizationId = Org, Name = "Autotask", Provider = ProviderType.AutotaskPsa,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x",
        });
        var company = new ClientCompany { MspOrganizationId = Org, PsaConnectionId = Conn, Name = "Acme", ExternalCompanyId = "1" };
        h.Db.ClientCompanies.Add(company);
        var ticket = new Ticket
        {
            MspOrganizationId = Org, ClientCompanyId = company.Id, PsaConnectionId = Conn,
            Title = "t", RequesterName = "r", RequesterEmail = "r@a.test", ExternalTicketId = "7814",
        };
        h.Db.Tickets.Add(ticket);

        // The row as it looked after the FIRST failure — the message that was true then.
        var entry = new TicketTimeEntry
        {
            MspOrganizationId = Org, TicketId = ticket.Id, Hours = 1m, Billable = true,
            Source = TimeEntrySource.Portal, SyncStatus = TimeEntrySyncStatus.Failed,
            SyncError = "Autotask needs a technician to own the time entry, and rejects API-only users.",
            EntryDate = h.Clock.GetUtcNow(),
        };
        h.Db.TicketTimeEntries.Add(entry);
        await h.Db.SaveChangesAsync();

        const string providerSaid = "Autotask rejected the entry: the resource is not eligible for this work role.";
        var connector = new StubConnector
        {
            TimeEntryFailure = new ConnectorException(ConnectorFailureKind.InvalidRequest, providerSaid),
        };
        // `admin` is unused on the retry path (it serves work-type labels and the options endpoint).
        var controller = new TicketTimeController(
            h.Db, new FakeResolver(connector), null!, new NoopTicketScopeQuery(), new TestUser(Guid.NewGuid()));

        var act = async () => await controller.Retry(ticket.Id, entry.Id, default);
        await act.Should().ThrowAsync<ValidationFailedException>();

        var stored = await h.Db.TicketTimeEntries.AsNoTracking().SingleAsync(t => t.Id == entry.Id);
        stored.SyncError.Should().Be(providerSaid,
            "the row must carry what the provider said THIS time, not the reason it failed the first time");
        stored.SyncStatus.Should().Be(TimeEntrySyncStatus.Failed);
    }
}
