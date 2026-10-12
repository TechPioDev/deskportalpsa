using Desk.Application.Common;
using Desk.Domain.Enums;
using Desk.Domain.Identity;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Admin;
using Desk.Infrastructure.Attachments;
using Desk.Infrastructure.Authorization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Mapping a staff user to their own resource in each PSA, so time logged from the portal lands on
/// THEIR timesheet instead of the connection's default resource. Per connection, because the same
/// person has a different identifier in every PSA — and the identifiers are not even the same kind
/// of value, so one shared field would be wrong for one provider.
/// </summary>
public class PsaIdentityTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static (AdminHarness H, UserAdminService Svc) Build()
    {
        var h = AdminHarness.Create(Org);
        var svc = new UserAdminService(h.Db, new AuditWriter(h.Db, h.User, h.Tenant, h.Clock), h.Tenant, h.User,
            new InMemoryObjectStorage(new AttachmentStorageOptions(), h.Clock), new EffectivePermissionService(h.Db), h.Clock);
        return (h, svc);
    }

    private static Guid AddConnection(AdminHarness h, string name, ProviderType provider)
    {
        var id = Guid.NewGuid();
        h.Db.PsaConnections.Add(new PsaConnection
        {
            Id = id, MspOrganizationId = Org, Name = name, Provider = provider,
            ApiEndpoint = "https://x", CredentialSecretRef = "mem://x", IsEnabled = true,
        });
        return id;
    }

    private static Guid AddUser(AdminHarness h, string email)
    {
        var id = Guid.NewGuid();
        h.Db.AppUsers.Add(new AppUser { Id = id, MspOrganizationId = Org, Email = email, DisplayName = email });
        return id;
    }

    [Fact]
    public async Task A_user_maps_to_a_different_identifier_in_each_psa()
    {
        // The whole reason this is per-connection: Autotask wants a numeric resourceID and
        // ConnectWise a member identifier string. One field could not hold both.
        var (h, svc) = Build();
        var at = AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var cw = AddConnection(h, "CWM", ProviderType.ConnectWisePsa);
        var user = AddUser(h, "harpal@techpio.test");
        await h.Db.SaveChangesAsync();

        await svc.SetPsaIdentityAsync(user, at, "29682885", "Harpal Singh");
        await svc.SetPsaIdentityAsync(user, cw, "hsingh", "Harpal Singh");

        var rows = await svc.PsaIdentitiesAsync(user);
        rows.Should().HaveCount(2);
        rows.Single(r => r.ConnectionName == "Autotask").ExternalTechnicianId.Should().Be("29682885");
        rows.Single(r => r.ConnectionName == "CWM").ExternalTechnicianId.Should().Be("hsingh");
    }

    [Fact]
    public async Task Setting_it_twice_replaces_rather_than_stacking()
    {
        // Two rows for one person on one connection would make "who is this user in Autotask"
        // ambiguous exactly where the answer decides whose timesheet an hour lands on.
        var (h, svc) = Build();
        var at = AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var user = AddUser(h, "harpal@techpio.test");
        await h.Db.SaveChangesAsync();

        await svc.SetPsaIdentityAsync(user, at, "29682885", "Harpal Singh");
        await svc.SetPsaIdentityAsync(user, at, "29682999", "Harpal Singh (new)");

        (await h.Db.UserPsaIdentities.CountAsync()).Should().Be(1);
        (await svc.PsaIdentitiesAsync(user)).Single().ExternalTechnicianId.Should().Be("29682999");
    }

    [Fact]
    public async Task Clearing_the_mapping_returns_the_user_to_the_connection_default()
    {
        // Clearing is a real choice, not a failure state — an unmapped user's time falls back to
        // the connection's default resource, which is how every entry behaved before this existed.
        var (h, svc) = Build();
        var at = AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var user = AddUser(h, "harpal@techpio.test");
        await h.Db.SaveChangesAsync();
        await svc.SetPsaIdentityAsync(user, at, "29682885", "Harpal Singh");

        await svc.SetPsaIdentityAsync(user, at, "   ", null);

        (await h.Db.UserPsaIdentities.AnyAsync()).Should().BeFalse();
        (await svc.PsaIdentitiesAsync(user)).Single().ExternalTechnicianId.Should().BeNull();
    }

    [Fact]
    public async Task Every_enabled_connection_is_listed_even_with_no_mapping_yet()
    {
        // The page has to show a row you can fill in; listing only the mapped ones would hide the
        // connection an admin actually came to configure.
        var (h, svc) = Build();
        AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        AddConnection(h, "CWM", ProviderType.ConnectWisePsa);
        var user = AddUser(h, "new@techpio.test");
        await h.Db.SaveChangesAsync();

        var rows = await svc.PsaIdentitiesAsync(user);

        rows.Select(r => r.ConnectionName).Should().BeEquivalentTo(["Autotask", "CWM"]);
        rows.Should().OnlyContain(r => r.ExternalTechnicianId == null);
    }

    [Fact]
    public async Task A_PSA_login_belongs_to_one_person_on_a_connection()
    {
        // A link says "this login IS this person": tickets assigned to it are theirs to see, and
        // work done under it is credited to them. With two people on one login the later link won
        // without a word, and listing the connection's technicians failed outright.
        var (h, svc) = Build();
        var at = AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var harpal = AddUser(h, "harpal@techpio.test");
        var anika = AddUser(h, "anika@techpio.test");
        await h.Db.SaveChangesAsync();
        await svc.SetPsaIdentityAsync(harpal, at, "29682885", "Harpal Singh");

        var act = () => svc.SetPsaIdentityAsync(anika, at, "29682885", "Harpal Singh");

        (await act.Should().ThrowAsync<ValidationFailedException>()).WithMessage("*already linked to harpal@techpio.test*");
        (await h.Db.UserPsaIdentities.SingleAsync()).AppUserId.Should().Be(harpal);
    }

    [Fact]
    public async Task The_same_login_id_on_another_connection_is_free_and_saving_ones_own_link_again_is_fine()
    {
        var (h, svc) = Build();
        var first = AddConnection(h, "ConnectWise - A", ProviderType.ConnectWisePsa);
        var second = AddConnection(h, "ConnectWise - B", ProviderType.ConnectWisePsa);
        var harpal = AddUser(h, "harpal@techpio.test");
        var anika = AddUser(h, "anika@techpio.test");
        await h.Db.SaveChangesAsync();

        await svc.SetPsaIdentityAsync(harpal, first, "5", "Harpal");
        await svc.SetPsaIdentityAsync(anika, second, "5", "Anika");      // member 5 of another account is somebody else
        await svc.SetPsaIdentityAsync(harpal, first, "5", "Harpal S.");  // re-saving your own

        (await h.Db.UserPsaIdentities.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Once_a_link_is_removed_the_login_can_be_given_to_someone_else()
    {
        var (h, svc) = Build();
        var at = AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var harpal = AddUser(h, "harpal@techpio.test");
        var anika = AddUser(h, "anika@techpio.test");
        await h.Db.SaveChangesAsync();
        await svc.SetPsaIdentityAsync(harpal, at, "29682885", "Harpal Singh");

        await svc.SetPsaIdentityAsync(harpal, at, null, null);
        await svc.SetPsaIdentityAsync(anika, at, "29682885", "Anika");

        (await h.Db.UserPsaIdentities.SingleAsync()).AppUserId.Should().Be(anika);
    }

    [Fact]
    public async Task A_user_from_another_tenant_is_not_found()
    {
        var (h, svc) = Build();
        AddConnection(h, "Autotask", ProviderType.AutotaskPsa);
        var outsider = Guid.NewGuid();
        h.Db.AppUsers.Add(new AppUser
        { Id = outsider, MspOrganizationId = Guid.NewGuid(), Email = "x@other.test", DisplayName = "X" });
        await h.Db.SaveChangesAsync();

        var act = async () => await svc.PsaIdentitiesAsync(outsider);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
