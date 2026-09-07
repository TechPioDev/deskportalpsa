using System.Net.Mail;
using Desk.Application.Marketing;
using Desk.Domain.Marketing;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.Marketing;

/// <summary>
/// Inbound enquiries from the public site. Submission is anonymous, so everything it accepts is
/// treated as hostile: trimmed, length-checked, and never echoed back to the caller.
///
/// Over-long fields are REFUSED, not cut to fit. Clipping was worse than it looks: the sender saw
/// an ordinary "thank you" while the tail of what they wrote was discarded, and staff opened a
/// message ending mid-sentence with nothing to say it had been shortened — so the reply answered a
/// question the visitor never finished asking, and neither side could tell why.
/// </summary>
public sealed class EnquiryService(DeskDbContext db, TimeProvider clock) : IEnquiryService
{
    /// <summary>Trimmed, or null when nothing is left. No limit is applied here — length is
    /// something to check and report, not something to impose silently.</summary>
    private static string? Trimmed(string? value)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string? Clip(string? value, int max)
    {
        var v = Trimmed(value);
        return v is null || v.Length <= max ? v : v[..max];
    }

    private static bool LooksLikeEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed) && parsed.Host.Contains('.');

    public async Task<SubmitEnquiryResult> SubmitAsync(SubmitEnquiryInput input, CancellationToken ct = default)
    {
        // Honeypot tripped: answer as though it worked. Telling a bot it was caught only teaches it
        // which field to leave alone next time.
        if (!string.IsNullOrWhiteSpace(input.Website)) return SubmitEnquiryResult.Ok;

        var name = Trimmed(input.Name);
        var email = Trimmed(input.Email);
        var message = Trimmed(input.Message);
        var company = Trimmed(input.Company);
        var phone = Trimmed(input.Phone);
        var preferred = Trimmed(input.PreferredTime);

        // Length first, before anything parses these strings: this endpoint is anonymous, and a
        // value that is megabytes long should be turned away on its size rather than handed to an
        // address parser. Trimming happens first, so trailing whitespace never costs a visitor a
        // rejection they cannot see.
        foreach (var (value, label, max) in new[]
        {
            (name, "name", Enquiry.NameMax),
            (email, "email address", Enquiry.EmailMax),
            (company, "company", Enquiry.CompanyMax),
            (phone, "phone number", Enquiry.PhoneMax),
            (message, "message", Enquiry.MessageMax),
            (preferred, "preferred time", Enquiry.PreferredTimeMax),
        })
        {
            if (value is not null && value.Length > max)
                return SubmitEnquiryResult.TooLong(label, max);
        }

        if (name is null || email is null || message is null || !LooksLikeEmail(email))
            return SubmitEnquiryResult.Incomplete;

        // A meeting request needs someone reachable and a time to aim for; a general question does
        // not. The browser marks the same fields required, but that is a courtesy to the visitor —
        // this endpoint is anonymous and anything arriving here may have skipped the form entirely.
        if (input.Kind == EnquiryKind.Meeting && (company is null || phone is null || preferred is null))
            return SubmitEnquiryResult.Incomplete;

        db.Enquiries.Add(new Enquiry
        {
            Kind = input.Kind,
            Name = name,
            Email = email,
            Company = company,
            Phone = phone,
            Message = message,
            PreferredTime = preferred,
            // The one field still clipped, and the only one nobody typed: the site fills it in.
            // Refusing a genuine enquiry because our own page path grew too long would throw away
            // the lead over context that is merely nice to have.
            SourcePage = Clip(input.SourcePage, Enquiry.SourcePageMax),
            Status = EnquiryStatus.New,
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return SubmitEnquiryResult.Ok;
    }

    public async Task<EnquiryListResult> ListAsync(EnquiryStatus? status = null, CancellationToken ct = default)
    {
        var q = db.Enquiries.AsNoTracking();
        var total = await q.CountAsync(ct);
        var newCount = await q.CountAsync(e => e.Status == EnquiryStatus.New, ct);
        if (status is { } s) q = q.Where(e => e.Status == s);

        var items = await q
            .OrderByDescending(e => e.CreatedAt)
            .Take(200)
            .Select(e => new EnquiryDto(
                e.Id, e.Kind, e.Name, e.Email, e.Company, e.Phone, e.Message,
                e.PreferredTime, e.Status, e.SourcePage, e.CreatedAt))
            .ToListAsync(ct);

        return new EnquiryListResult(total, newCount, items);
    }

    public async Task<bool> SetStatusAsync(Guid id, EnquiryStatus status, CancellationToken ct = default)
    {
        var row = await db.Enquiries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return false;
        row.Status = status;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }
}
