using System.Net;
using System.Text;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Domain.Enums;
using Desk.Domain.Tickets;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Admin;

/// <summary>
/// The "needs attention" rules. Each one is a condition that was, at some point, found the hard
/// way on this installation: a connection marked Degraded that nobody looked at for a week, closed
/// tickets that never got a closed date and so vanished from resolution-time reports, scheduled
/// reports quietly saved-but-not-emailed because the mail account was never set up.
///
/// Read-only against the database, so the list is cheap enough to compute on every page load and
/// every digest; the digest is the same list, sent once a day, only when it is not empty.
/// </summary>
public sealed class AttentionService(
    DeskDbContext db,
    ITenantContext tenant,
    ITicketResyncService resync,
    IEmailSender email,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<AttentionService> logger) : IAttentionService
{
    /// <summary>Two-way connections poll every five minutes; an hour without a completed run is a stall, not a quiet period.</summary>
    public static readonly TimeSpan StaleSyncAfter = TimeSpan.FromHours(1);

    /// <summary>A report that failed to send stops being news after this long.</summary>
    public static readonly TimeSpan UndeliveredReportWindow = TimeSpan.FromDays(14);

    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    private static string Clip(string text) => text.Length > 60 ? text[..60] + "…" : text;

    public async Task<AttentionDto> ListAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var items = new List<AttentionItem>();

        // ── Connections ───────────────────────────────────────────────────────
        var connections = await db.PsaConnections.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.Provider, c.Status, c.TwoWaySync, c.LastSuccessfulSyncAt, c.LastError })
            .ToListAsync(ct);
        foreach (var c in connections)
        {
            if (c.Status is ConnectionStatus.Failed or ConnectionStatus.Degraded)
            {
                items.Add(new AttentionItem(
                    "connection-" + (c.Status == ConnectionStatus.Failed ? "failed" : "degraded"),
                    c.Status == ConnectionStatus.Failed ? "critical" : "warning",
                    $"{ProviderName(c.Provider)} connection \"{c.Name}\" is {c.Status.ToString().ToLowerInvariant()}",
                    string.IsNullOrWhiteSpace(c.LastError)
                        ? "The last sync or health check did not succeed. Open the connection and run Test connection."
                        : $"Last error: {c.LastError.Trim()}",
                    1, "/dashboard/connections"));
            }

            if (c.TwoWaySync && c.Status is not ConnectionStatus.Disabled
                && (c.LastSuccessfulSyncAt is null || now - c.LastSuccessfulSyncAt.Value > StaleSyncAfter))
            {
                items.Add(new AttentionItem(
                    "sync-stale", "warning",
                    c.LastSuccessfulSyncAt is null
                        ? $"\"{c.Name}\" has never completed a sync"
                        : $"\"{c.Name}\" has not synced for {Age(now - c.LastSuccessfulSyncAt.Value)}",
                    "Tickets, notes and status changes made in the PSA are not reaching the portal. " +
                    "If the worker is running, open the connection and run Sync now to see the error.",
                    1, "/dashboard/connections"));
            }
        }

        // ── SLA: breached, about to breach, and replies owed ──────────────────
        // Open tickets with a due date that somebody on the desk can act on. Waiting on the customer
        // or on hold is excluded, as is a board ticket whose clock is paused: nobody can fix those
        // by working harder, and listing them buries the ones that can be.
        var atRiskBy = now.AddHours(TicketStatusRules.AtRiskHours);
        var actionable = db.Tickets.AsNoTracking()
            .Where(TicketStatusRules.Open())
            .Where(t => t.SlaPausedAt == null
                && !t.PortalStatus.ToUpper().Contains("WAITING")
                && !t.PortalStatus.ToUpper().Contains("HOLD"));
        var breached = await actionable
            .Where(t => t.SlaDueAt != null && t.SlaDueAt < now)
            .OrderBy(t => t.SlaDueAt)
            .Select(t => new { Ref = t.Number ?? t.ExternalTicketId, t.Title, Due = t.SlaDueAt!.Value })
            .ToListAsync(ct);
        if (breached.Count > 0)
        {
            var oldest = breached[0];
            // The Overdue list this links to shows every late open ticket, including those waiting on
            // the customer; say how many this count left out, so the two numbers do not look like a bug.
            var waitingLate = await db.Tickets.AsNoTracking()
                .Where(TicketStatusRules.Open())
                .Where(t => t.SlaPausedAt == null && t.SlaDueAt != null && t.SlaDueAt < now
                    && (t.PortalStatus.ToUpper().Contains("WAITING") || t.PortalStatus.ToUpper().Contains("HOLD")))
                .CountAsync(ct);
            items.Add(new AttentionItem(
                "sla-breached", "critical",
                $"{Plural(breached.Count, "open ticket")} past {(breached.Count == 1 ? "its" : "their")} SLA",
                $"Longest overdue: {oldest.Ref ?? "a ticket"} \u201c{Clip(oldest.Title)}\u201d, due {Age(now - oldest.Due)} ago. " +
                (waitingLate > 0 ? $"Not counted: {waitingLate} more waiting on the customer or on hold. " : "") +
                "Resolve them, or set a realistic date with the client — a date everyone knows is wrong measures nothing.",
                breached.Count, "/dashboard/tickets?overdue=1"));
        }
        var atRisk = await actionable
            .Where(t => t.SlaDueAt != null && t.SlaDueAt >= now && t.SlaDueAt <= atRiskBy)
            .OrderBy(t => t.SlaDueAt)
            .Select(t => new { Ref = t.Number ?? t.ExternalTicketId, t.Title, Due = t.SlaDueAt!.Value })
            .ToListAsync(ct);
        if (atRisk.Count > 0)
        {
            var next = atRisk[0];
            items.Add(new AttentionItem(
                "sla-at-risk", "warning",
                $"{Plural(atRisk.Count, "ticket")} will breach {(atRisk.Count == 1 ? "its" : "their")} SLA within {TicketStatusRules.AtRiskHours} hours",
                $"Next: {next.Ref ?? "a ticket"} \u201c{Clip(next.Title)}\u201d, due in {Age(next.Due - now)}.",
                atRisk.Count, "/dashboard/tickets?due=soon"));
        }
        var replyOwed = await actionable
            .Where(t => t.FirstResponseDueAt != null && t.FirstResponseDueAt < now && t.FirstRespondedAt == null)
            .OrderBy(t => t.FirstResponseDueAt)
            .Select(t => new { Ref = t.Number ?? t.ExternalTicketId, t.Title, t.BoardId })
            .ToListAsync(ct);
        if (replyOwed.Count > 0)
        {
            var first = replyOwed[0];
            items.Add(new AttentionItem(
                "sla-reply-overdue", "warning",
                $"{Plural(replyOwed.Count, "board ticket")} still waiting for a first reply",
                $"Oldest: {first.Ref ?? "a ticket"} \u201c{Clip(first.Title)}\u201d. A note from anyone on the team counts as the reply.",
                replyOwed.Count, first.BoardId is { } b ? $"/dashboard/boards/{b}" : "/dashboard/boards"));
        }

        // ── Unhappy clients ───────────────────────────────────────────────────
        // A rating of 1 or 2 in the last week is a conversation somebody should have while the
        // client still remembers the ticket, not a number to find in next quarter's review.
        var weekAgo = now.AddDays(-7);
        var poor = await db.TicketSatisfactions.AsNoTracking()
            .Where(s => s.RatedAt >= weekAgo && s.Rating <= 2)
            .OrderByDescending(s => s.RatedAt)
            .Select(s => new
            {
                s.Rating, s.Comment,
                Client = db.ClientCompanies.Where(c => c.Id == s.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                Reference = db.Tickets.Where(t => t.Id == s.TicketId).Select(t => t.Number ?? t.ExternalTicketId).FirstOrDefault(),
            })
            .ToListAsync(ct);
        if (poor.Count > 0)
        {
            var latest = poor[0];
            items.Add(new AttentionItem(
                "satisfaction-poor", "warning",
                $"{Plural(poor.Count, "poor rating")} from clients this week",
                $"Latest: {latest.Rating}/5 on {latest.Reference ?? "a ticket"}{(latest.Client is null ? "" : $" from {latest.Client}")}" +
                (latest.Comment is null ? "." : $" — \u201c{(latest.Comment.Length > 140 ? latest.Comment[..140] + "…" : latest.Comment)}\u201d"),
                poor.Count, "/dashboard/analytics/satisfaction"));
        }

        // ── Approvals nobody has answered ─────────────────────────────────────
        // The ticket waits on the customer while an approval is open, so its SLA is paused and it is
        // on no overdue list. Without this, a question the approver never saw would sit there quietly
        // for as long as nobody looked - the pause is exactly what hides it.
        var approvalCutoff = now.AddDays(-2);
        var unanswered = await db.TicketApprovals.AsNoTracking()
            .Where(a => a.State == ApprovalState.Pending && a.RequestedAt <= approvalCutoff)
            .OrderBy(a => a.RequestedAt)
            .Select(a => new
            {
                a.TicketId, a.ApproverName, a.RequestedAt,
                Client = db.ClientCompanies.Where(c => c.Id == a.ClientCompanyId).Select(c => c.Name).FirstOrDefault(),
                Reference = db.Tickets.Where(t => t.Id == a.TicketId).Select(t => t.Number ?? t.ExternalTicketId).FirstOrDefault(),
            })
            .ToListAsync(ct);
        if (unanswered.Count > 0)
        {
            var oldest = unanswered[0];
            var days = Math.Max(2, (int)(now - oldest.RequestedAt).TotalDays);
            items.Add(new AttentionItem(
                "approval-waiting", "warning",
                $"{Plural(unanswered.Count, "approval")} waiting more than 2 days",
                $"Oldest: {oldest.Reference ?? "a ticket"} has waited {days} days for {oldest.ApproverName}" +
                $"{(oldest.Client is null ? "" : $" at {oldest.Client}")}. Chase them by phone and record the answer, or withdraw the request.",
                unanswered.Count, $"/dashboard/tickets/{oldest.TicketId}"));
        }

        // ── Background jobs (inbound events, pushes retried by the worker) ────
        var deadLettered = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Status == BackgroundJobStatus.DeadLettered)
            .OrderByDescending(j => j.UpdatedAt)
            .Select(j => new { j.JobType, j.LastError })
            .ToListAsync(ct);
        if (deadLettered.Count > 0)
        {
            items.Add(new AttentionItem(
                "jobs-dead-lettered", "critical",
                $"{Plural(deadLettered.Count, "background job")} gave up after retries",
                FirstError(deadLettered.Select(j => j.LastError)) ?? "Open Jobs to see the errors and reprocess them.",
                deadLettered.Count, "/dashboard/jobs"));
        }
        var failing = await db.BackgroundJobs.AsNoTracking()
            .CountAsync(j => j.Status == BackgroundJobStatus.Failed, ct);
        if (failing > 0)
        {
            items.Add(new AttentionItem(
                "jobs-failing", "warning",
                $"{Plural(failing, "background job")} failed and will be retried",
                "They retry on their own; this becomes critical if they run out of attempts.",
                failing, "/dashboard/jobs"));
        }

        // ── Tickets the portal holds that never reached the PSA ───────────────
        var unsynced = await resync.ListAsync(null, ct);
        if (unsynced.Count > 0)
        {
            items.Add(new AttentionItem(
                "tickets-unsynced", "critical",
                $"{Plural(unsynced.Count, "ticket")} never reached the PSA",
                FirstError(unsynced.Tickets.Select(t => t.SyncError))
                    ?? "Customers raised them in the portal but the PSA rejected the create. Resync them from Integration Health.",
                unsynced.Count, "/dashboard/health"));
        }

        // ── Closed tickets with no closed date ────────────────────────────────
        // Resolution-time, SLA and "closed this period" figures all key on ClosedAt. A PSA status
        // that closes a ticket in the portal without a date (ConnectWise: a board status whose
        // "Closed" flag is off, such as Completed) makes every one of those reports skip it.
        var closedWithoutDate = await db.Tickets.AsNoTracking()
            .Where(t => t.ClosedAt == null && t.ResolvedAt == null
                        && (t.PortalStatus.Contains("CLOSED") || t.PortalStatus.Contains("RESOLV")))
            .GroupBy(t => t.PsaStatus ?? "")
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);
        if (closedWithoutDate.Count > 0)
        {
            var total = closedWithoutDate.Sum(g => g.Count);
            var statuses = string.Join(", ", closedWithoutDate.Take(3).Select(g => $"\"{(g.Status.Length == 0 ? "(no PSA status)" : g.Status)}\" ×{g.Count}"));
            items.Add(new AttentionItem(
                "closed-without-date", "warning",
                $"{Plural(total, "closed ticket")} {(total == 1 ? "has" : "have")} no closed date",
                $"Resolution time, SLA and closed-per-period reports leave them out. PSA status: {statuses}. " +
                "Usually the PSA status closes the ticket without marking it closed (ConnectWise: turn on the board status's Closed flag, or map it to Resolved).",
                total, "/dashboard/mappings"));
        }

        // ── Reports that were generated but not emailed ───────────────────────
        var since = now - UndeliveredReportWindow;
        var staffUndelivered = await db.StaffReportRuns.AsNoTracking()
            .Where(r => !r.Delivered && r.StaffReportScheduleId != null && r.GeneratedAt >= since)
            .OrderByDescending(r => r.GeneratedAt)
            .Select(r => new { r.Title, r.DeliveryNote })
            .ToListAsync(ct);
        if (staffUndelivered.Count > 0)
        {
            items.Add(new AttentionItem(
                "staff-reports-undelivered", "warning",
                $"{Plural(staffUndelivered.Count, "scheduled report")} {(staffUndelivered.Count == 1 ? "was" : "were")} not emailed",
                $"Latest: {staffUndelivered[0].Title} — {staffUndelivered[0].DeliveryNote ?? "no reason recorded"}. The reports are still available in the portal.",
                staffUndelivered.Count, "/dashboard/reports"));
        }
        var clientUndelivered = await db.ReportRuns.AsNoTracking()
            .Where(r => !r.Delivered && r.ReportScheduleId != null && r.GeneratedAt >= since)
            .OrderByDescending(r => r.GeneratedAt)
            .Select(r => new { r.DeliveryNote, Client = r.ClientCompany != null ? r.ClientCompany.Name : null })
            .ToListAsync(ct);
        if (clientUndelivered.Count > 0)
        {
            items.Add(new AttentionItem(
                "client-reports-undelivered", "warning",
                $"{Plural(clientUndelivered.Count, "client report")} {(clientUndelivered.Count == 1 ? "was" : "were")} not emailed",
                $"Latest: {clientUndelivered[0].Client ?? "a client"} — {clientUndelivered[0].DeliveryNote ?? "no reason recorded"}.",
                clientUndelivered.Count, "/dashboard/reports"));
        }

        // ── Email delivery, only once something depends on it ─────────────────
        var status = await email.StatusAsync(Org, ct);
        if (!status.Configured)
        {
            var schedules = await db.StaffReportSchedules.CountAsync(s => s.IsEnabled, ct)
                          + await db.ReportSchedules.CountAsync(s => s.IsEnabled, ct);
            if (schedules > 0)
            {
                items.Add(new AttentionItem(
                    "email-not-configured", "warning",
                    "Email delivery is not set up",
                    $"{Plural(schedules, "report schedule")} will generate reports that nobody receives. Add the mail account under Email delivery.",
                    schedules, "/dashboard/health"));
            }
        }

        var org = await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == Org)
            .Select(o => new { o.AttentionDigestRecipients, o.AttentionDigestSentOn })
            .FirstOrDefaultAsync(ct);

        // Critical first, then by how many things each item stands for.
        var ordered = items
            .OrderBy(i => i.Severity == "critical" ? 0 : 1)
            .ThenByDescending(i => i.Count)
            .ToList();
        return new AttentionDto(ordered, new AttentionDigestDto(org?.AttentionDigestRecipients, org?.AttentionDigestSentOn), now);
    }

    public async Task<(AttentionDigestDto Digest, IReadOnlyList<string> Invalid)> SetDigestRecipientsAsync(string? recipients, CancellationToken ct = default)
    {
        var org = await db.MspOrganizations.FirstOrDefaultAsync(o => o.Id == Org, ct)
            ?? throw new NotFoundException("Organization");
        var (valid, invalid) = EmailAddresses.Parse(recipients);
        if (valid.Count > EmailAddresses.MaxRecipients)
            throw new ValidationFailedException($"At most {EmailAddresses.MaxRecipients} recipients.");

        var before = org.AttentionDigestRecipients;
        org.AttentionDigestRecipients = valid.Count == 0 ? null : string.Join(", ", valid);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("attention.digest.recipients", "MspOrganization", org.Id.ToString(),
            new { before, after = org.AttentionDigestRecipients, invalid }, ct);
        return (new AttentionDigestDto(org.AttentionDigestRecipients, org.AttentionDigestSentOn), invalid);
    }

    public async Task<AttentionDigestSendResult> SendDigestNowAsync(CancellationToken ct = default)
    {
        var list = await ListAsync(ct);
        var (to, _) = EmailAddresses.Parse(list.Digest.Recipients);
        if (to.Count == 0) return new(false, "No digest recipients set.");
        if (!(await email.StatusAsync(Org, ct)).Configured) return new(false, "Email delivery is not configured.");

        var orgName = await db.MspOrganizations.AsNoTracking().Where(o => o.Id == Org).Select(o => o.Name).FirstAsync(ct);
        try
        {
            await email.SendAsync(Org, Compose(orgName, list.Items, list.CheckedAt, PortalBase()) with { To = to }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Attention digest send failed for organization {Org}", Org);
            return new(false, $"Could not send: {ex.Message}");
        }
        await audit.WriteAsync("attention.digest.sent", "MspOrganization", Org.ToString(), new { to = to.Count, items = list.Items.Count, manual = true }, ct);
        return new(true, list.Items.Count == 0
            ? $"Sent an all-clear digest to {Plural(to.Count, "recipient")}."
            : $"Sent {Plural(list.Items.Count, "item")} to {Plural(to.Count, "recipient")}.");
    }

    /// <summary>The digest email. Plain text carries everything; the HTML is the same list, readable on a phone.</summary>
    public static EmailMessage Compose(string orgName, IReadOnlyList<AttentionItem> items, DateTimeOffset checkedAt, string portalBase)
    {
        var critical = items.Count(i => i.Severity == "critical");
        var subject = items.Count == 0
            ? $"Desk Portal: all clear — {orgName}"
            : $"Desk Portal: {Plural(items.Count, "item")} need{(items.Count == 1 ? "s" : "")} attention — {orgName}"
              + (critical > 0 ? $" ({critical} critical)" : "");

        var text = new StringBuilder();
        var html = new StringBuilder();
        text.AppendLine(items.Count == 0
            ? "Nothing needs attention right now. Connections are syncing, pushes are getting through and reports are going out."
            : "These need someone to look at them:");
        text.AppendLine();
        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2933;max-width:640px\">");
        html.Append($"<p>{(items.Count == 0 ? "Nothing needs attention right now. Connections are syncing, pushes are getting through and reports are going out." : "These need someone to look at them:")}</p>");
        if (items.Count > 0)
        {
            html.Append("<ol style=\"padding-left:20px\">");
            foreach (var i in items)
            {
                var link = i.Link is null ? null : portalBase.TrimEnd('/') + i.Link;
                text.AppendLine($"[{i.Severity.ToUpperInvariant()}] {i.Title}");
                text.AppendLine($"    {i.Detail}");
                if (link is not null) text.AppendLine($"    {link}");
                text.AppendLine();

                var color = i.Severity == "critical" ? "#b42318" : "#b54708";
                html.Append("<li style=\"margin:0 0 12px\">")
                    .Append($"<span style=\"display:inline-block;padding:1px 6px;border-radius:4px;background:{color};color:#fff;font-size:11px;font-weight:600;letter-spacing:.02em\">{i.Severity.ToUpperInvariant()}</span> ")
                    .Append($"<strong>{WebUtility.HtmlEncode(i.Title)}</strong>")
                    .Append($"<div style=\"color:#52606d;margin-top:2px\">{WebUtility.HtmlEncode(i.Detail)}</div>");
                if (link is not null)
                    html.Append($"<div style=\"margin-top:2px\"><a href=\"{WebUtility.HtmlEncode(link)}\">Open in the portal</a></div>");
                html.Append("</li>");
            }
            html.Append("</ol>");
        }
        text.AppendLine($"Checked {checkedAt:yyyy-MM-dd HH:mm} UTC. Integration Health: {portalBase.TrimEnd('/')}/dashboard/health");
        html.Append($"<p style=\"color:#7b8794;font-size:12px\">Checked {checkedAt:yyyy-MM-dd HH:mm} UTC · <a href=\"{WebUtility.HtmlEncode(portalBase.TrimEnd('/'))}/dashboard/health\">Integration Health</a></p></div>");
        return new EmailMessage([], subject, text.ToString(), html.ToString());
    }

    private static string PortalBase()
        => Environment.GetEnvironmentVariable("PORTAL_PUBLIC_URL")?.TrimEnd('/') is { Length: > 0 } u ? u : "https://piomanage.com";

    private static string ProviderName(ProviderType p) => p switch
    {
        ProviderType.ConnectWisePsa => "ConnectWise",
        ProviderType.AutotaskPsa => "Autotask",
        ProviderType.HaloPsa => "HaloPSA",
        _ => p.ToString(),
    };

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string? FirstError(IEnumerable<string?> errors)
    {
        var e = errors.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        if (e is null) return null;
        return e.Length > 240 ? e[..240] + "…" : e;
    }

    private static string Age(TimeSpan t)
        => t.TotalDays >= 2 ? $"{(int)t.TotalDays} days"
         : t.TotalHours >= 1 ? $"{(int)t.TotalHours} hour{((int)t.TotalHours == 1 ? "" : "s")}"
         : $"{Math.Max(1, (int)t.TotalMinutes)} minutes";
}

/// <summary>
/// Sends each organization's digest once a day at 07:30 in its own time zone, after the 07:00
/// scheduled reports have had their chance to fail. Silent when there is nothing to report: an
/// "all clear" every morning trains people to delete the message before the morning it matters.
/// </summary>
public sealed class AttentionDigestRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<AttentionDigestRunner> logger) : IAttentionDigestRunner
{
    public static readonly TimeOnly SendAfter = new(7, 30);

    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        List<(Guid Id, string Name, string TimeZone, string Recipients, DateOnly? SentOn)> orgs;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            orgs = (await db.MspOrganizations.AsNoTracking()
                    .Where(o => o.IsActive && o.AttentionDigestRecipients != null)
                    .Select(o => new { o.Id, o.Name, o.TimeZone, o.AttentionDigestRecipients, o.AttentionDigestSentOn })
                    .ToListAsync(ct))
                .Select(o => (o.Id, o.Name, o.TimeZone, o.AttentionDigestRecipients!, o.AttentionDigestSentOn)).ToList();
        }

        var sent = 0;
        foreach (var org in orgs)
        {
            var local = TimeZoneInfo.ConvertTime(now, StaffReportPeriods.Zone(org.TimeZone));
            var today = DateOnly.FromDateTime(local.DateTime);
            if (org.SentOn == today || TimeOnly.FromDateTime(local.DateTime) < SendAfter) continue;

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org.Id);
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var attention = scope.ServiceProvider.GetRequiredService<IAttentionService>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            try
            {
                var list = await attention.ListAsync(ct);
                var (to, _) = EmailAddresses.Parse(org.Recipients);
                if (list.Items.Count > 0 && to.Count > 0 && (await email.StatusAsync(org.Id, ct)).Configured)
                {
                    var message = AttentionService.Compose(org.Name, list.Items, list.CheckedAt, PortalBase()) with { To = to };
                    await email.SendAsync(org.Id, message, ct);
                    sent++;
                    logger.LogInformation("Attention digest sent for {Org}: {Items} item(s) to {Recipients} recipient(s)", org.Name, list.Items.Count, to.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Marked as done for today all the same: retrying every five minutes would send the
                // same failure to the log 200 times, and tomorrow's run reports whatever still stands.
                logger.LogError(ex, "Attention digest failed for organization {Org}", org.Name);
            }

            var row = await db.MspOrganizations.FirstOrDefaultAsync(o => o.Id == org.Id, ct);
            if (row is null) continue;
            row.AttentionDigestSentOn = today;
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    private static string PortalBase()
        => Environment.GetEnvironmentVariable("PORTAL_PUBLIC_URL")?.TrimEnd('/') is { Length: > 0 } u ? u : "https://piomanage.com";
}
