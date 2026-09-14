using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Domain.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Email;

public sealed class EmailSettingsService(
    DeskDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    ISecretStore secrets,
    IEmailSender sender,
    IAuditWriter audit,
    SmtpOptions server) : IEmailSettingsService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<EmailSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await db.OrganizationEmailSettings.AsNoTracking().FirstOrDefaultAsync(s => s.MspOrganizationId == Org, ct);
        var status = await sender.StatusAsync(Org, ct);
        return row is null
            ? new EmailSettingsDto(false, null, 587, "StartTls", null, false, null, null, status)
            : new EmailSettingsDto(true, row.Host, row.Port, row.Security, row.Username, row.PasswordSecretRef is not null,
                row.FromAddress, row.FromName, status);
    }

    public async Task<EmailSettingsDto> SaveAsync(EmailSettingsInput input, CancellationToken ct = default)
    {
        var host = (input.Host ?? "").Trim();
        if (host.Length == 0) throw new ValidationFailedException("Enter the mail server, for example smtp.office365.com.");
        if (host.Contains("://") || host.Contains('/') || host.Contains(' ') || host.Length > 253)
            throw new ValidationFailedException("Enter only the server name, without http:// or a path.");
        if (input.Port is < 1 or > 65535) throw new ValidationFailedException("The port must be between 1 and 65535.");
        var security = SmtpEmailSender.SecurityModes.FirstOrDefault(m => m.Equals(input.Security?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationFailedException("Choose the connection security: StartTls, SslOnConnect or None.");
        var (from, badFrom) = EmailAddresses.Parse(input.FromAddress);
        if (from.Count != 1 || badFrom.Count > 0) throw new ValidationFailedException("Enter one valid From address.");
        var username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        if (input.Password is { Length: > 500 }) throw new ValidationFailedException("The password is too long.");
        if (server.BlockPrivateHosts) await SmtpEmailSender.EnsurePublicHostAsync(host, ct);

        var existingRow = await db.OrganizationEmailSettings.FirstOrDefaultAsync(s => s.MspOrganizationId == Org, ct);
        var created = existingRow is null;
        var row = existingRow ?? new OrganizationEmailSettings { MspOrganizationId = Org, Host = host, FromAddress = from[0] };

        // Checked before anything is written, so a refused save leaves neither a half-updated row nor
        // an orphaned secret behind.
        var willHavePassword = input.Password is { Length: > 0 } || (input.Password is null && row.PasswordSecretRef is not null);
        if (username is not null && !willHavePassword)
            throw new ValidationFailedException("Enter the password for this username.");

        // Null keeps what is stored; the form sends null when the box is left blank, so editing the
        // server name never silently wipes a password nobody retyped.
        if (input.Password is not null)
        {
            if (input.Password.Length == 0)
            {
                if (row.PasswordSecretRef is { } old) await secrets.DeleteAsync(old, ct);
                row.PasswordSecretRef = null;
            }
            else
            {
                var data = new Dictionary<string, string> { ["Password"] = input.Password };
                row.PasswordSecretRef = row.PasswordSecretRef is { } existing
                    ? await secrets.RotateAsync(existing, data, ct)
                    : await secrets.WriteAsync($"email/{Org:N}", data, ct);
            }
        }
        row.Host = host;
        row.Port = input.Port;
        row.Security = security;
        row.Username = username;
        row.FromAddress = from[0];
        row.FromName = string.IsNullOrWhiteSpace(input.FromName) ? "Desk Portal" : input.FromName.Trim()[..Math.Min(input.FromName.Trim().Length, 100)];
        row.UpdatedByUserId = user.UserId;
        if (created) db.OrganizationEmailSettings.Add(row);
        await db.SaveChangesAsync(ct);

        // What changed, never the password - only whether one was supplied.
        await audit.WriteAsync(created ? "email.settings.created" : "email.settings.updated", "OrganizationEmailSettings", row.Id.ToString(),
            new { row.Host, row.Port, row.Security, HasUsername = username is not null, PasswordChanged = input.Password is not null, row.FromAddress }, ct);
        return await GetAsync(ct);
    }

    public async Task<EmailSettingsDto> RemoveAsync(CancellationToken ct = default)
    {
        var row = await db.OrganizationEmailSettings.FirstOrDefaultAsync(s => s.MspOrganizationId == Org, ct);
        if (row is not null)
        {
            if (row.PasswordSecretRef is { } secretRef) await secrets.DeleteAsync(secretRef, ct);
            db.OrganizationEmailSettings.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("email.settings.removed", "OrganizationEmailSettings", row.Id.ToString(), new { row.Host }, ct);
        }
        return await GetAsync(ct);
    }
}
