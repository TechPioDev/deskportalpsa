using System.Text;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Analytics;
using Desk.Application.Common;
using Desk.Application.Reporting;
using Desk.Domain.Reporting;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Desk.Infrastructure.Reporting;

/// <summary>Builds the report model for a period. Shared by on-demand runs, scheduled runs and the ad-hoc PDF.</summary>
public sealed class TechnicianReportBuilder(DeskDbContext db, ITechnicianMetricsService metrics, TimeProvider clock)
{
    public async Task<TechnicianReport> BuildAsync(
        Guid organizationId, DateTimeOffset from, DateTimeOffset to, DateOnly start, DateOnly end,
        string label, Guid? clientCompanyId, CancellationToken ct)
    {
        var org = await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => new { o.Name, o.TimeZone }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Organization");
        var client = clientCompanyId is { } cid
            ? await db.ClientCompanies.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Client")
            : null;

        var days = await metrics.DailyAsync(new MetricsFilter { From = from, To = to, ClientCompanyId = clientCompanyId }, ct);

        // One row per person across the period — the same grouping the Technician hours page uses,
        // so the emailed figures match what the recipient sees when they open the portal.
        var technicians = days
            .GroupBy(d => d.AppUserId is { } u ? "u:" + u : "x:" + (d.TechnicianExternalId ?? d.Name))
            .Select(g => new TechnicianReportRow(
                g.First().Name, g.Sum(d => d.Hours), g.Sum(d => d.BillableHours), g.Sum(d => d.Resolved),
                g.Sum(d => d.TicketsTouched), g.Count()))
            .OrderByDescending(t => t.Hours).ThenByDescending(t => t.Resolved).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perDay = days.GroupBy(d => d.Date)
            .Select(g => new TechnicianReportDay(g.Key, g.Sum(d => d.Hours), g.Sum(d => d.Resolved)))
            .OrderBy(d => d.Date).ToList();

        return new TechnicianReport(org.Name, label, start, end, client, technicians, perDay, clock.GetUtcNow(), org.TimeZone);
    }
}

public sealed class StaffReportService(
    DeskDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IAuditWriter audit,
    TimeProvider clock,
    TechnicianReportBuilder builder,
    ClientQbrBuilder qbrs,
    StaffReportGenerator generator) : IStaffReportService
{
    private Guid Org => tenant.OrganizationId ?? throw new TenantScopeMissingException();

    public async Task<string> TimeZoneAsync(CancellationToken ct = default)
        => await db.MspOrganizations.AsNoTracking().Where(o => o.Id == Org).Select(o => o.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";

    public async Task<string> SetTimeZoneAsync(string timeZoneId, CancellationToken ct = default)
    {
        var id = timeZoneId?.Trim() ?? "";
        if (!StaffReportPeriods.IsKnown(id)) throw new ValidationFailedException($"Unknown time zone: {id}.");

        var org = await db.MspOrganizations.FirstOrDefaultAsync(o => o.Id == Org, ct) ?? throw new NotFoundException("Organization");
        var before = org.TimeZone;
        org.TimeZone = id;
        var zone = StaffReportPeriods.Zone(id);
        var now = clock.GetUtcNow();
        foreach (var s in await db.StaffReportSchedules.ToListAsync(ct))
            s.NextRunAt = StaffReportPeriods.NextRun(s.Frequency, now, zone);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("organization.timezone.changed", "MspOrganization", Org.ToString(), new { From = before, To = id }, ct);
        return id;
    }

    public async Task<IReadOnlyList<(Guid Id, string Name)>> ClientsAsync(CancellationToken ct = default)
        => (await db.ClientCompanies.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            .Select(c => (c.Id, c.Name)).ToList();

    public async Task<IReadOnlyList<StaffReportScheduleDto>> SchedulesAsync(CancellationToken ct = default)
    {
        var rows = await db.StaffReportSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var names = await ClientNamesAsync(rows.Select(r => r.ClientCompanyId), ct);
        return rows.Select(s => Dto(s, names)).ToList();
    }

    public async Task<StaffReportScheduleDto> SaveScheduleAsync(StaffReportScheduleInput input, CancellationToken ct = default)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ValidationFailedException("Give the report a name.");
        if (name.Length > 200) throw new ValidationFailedException("The name is longer than 200 characters.");
        if (!Enum.IsDefined(input.Kind) || !Enum.IsDefined(input.Frequency))
            throw new ValidationFailedException("Unknown report type or frequency.");
        if (input.Kind == StaffReportKind.ClientQbr && input.ClientCompanyId is null)
            throw new ValidationFailedException("A quarterly business review is for one client — choose the client.");
        if (input.ClientCompanyId is { } cid && !await db.ClientCompanies.AnyAsync(c => c.Id == cid, ct))
            throw new ValidationFailedException("That client does not exist.");

        // Recipients are checked now, not at send time: a typo found at 7am by the worker reaches
        // nobody, while a typo found here is fixed by the person still looking at the form.
        var (valid, invalid) = EmailAddresses.Parse(input.Recipients);
        if (invalid.Count > 0) throw new ValidationFailedException($"Not a valid email address: {string.Join(", ", invalid)}.");
        if (valid.Count > EmailAddresses.MaxRecipients)
            throw new ValidationFailedException($"At most {EmailAddresses.MaxRecipients} recipients.");

        StaffReportSchedule schedule;
        if (input.Id is { } id)
        {
            schedule = await db.StaffReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Report schedule");
        }
        else
        {
            schedule = new StaffReportSchedule { Name = name, MspOrganizationId = Org, CreatedByUserId = user.UserId };
            db.StaffReportSchedules.Add(schedule);
        }

        var zone = await ZoneAsync(ct);
        var timingChanged = input.Id is null || schedule.Frequency != input.Frequency || (!schedule.IsEnabled && input.IsEnabled);
        schedule.Name = name;
        schedule.Kind = input.Kind;
        schedule.Frequency = input.Frequency;
        schedule.ClientCompanyId = input.ClientCompanyId;
        schedule.Recipients = valid.Count > 0 ? string.Join(", ", valid) : null;
        schedule.IsEnabled = input.IsEnabled;
        if (timingChanged) schedule.NextRunAt = StaffReportPeriods.NextRun(schedule.Frequency, clock.GetUtcNow(), zone);

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(input.Id is null ? "staffreport.schedule.created" : "staffreport.schedule.updated",
            "StaffReportSchedule", schedule.Id.ToString(),
            new { schedule.Name, schedule.Kind, schedule.Frequency, schedule.ClientCompanyId, Recipients = valid.Count, schedule.IsEnabled }, ct);

        var names = await ClientNamesAsync([schedule.ClientCompanyId], ct);
        return Dto(schedule, names);
    }

    public async Task DeleteScheduleAsync(Guid id, CancellationToken ct = default)
    {
        var schedule = await db.StaffReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Report schedule");
        db.StaffReportSchedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("staffreport.schedule.deleted", "StaffReportSchedule", id.ToString(), new { schedule.Name }, ct);
    }

    public async Task<StaffReportRunDto> RunNowAsync(Guid scheduleId, CancellationToken ct = default)
    {
        var schedule = await db.StaffReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct) ?? throw new NotFoundException("Report schedule");
        var run = await generator.GenerateAndDeliverAsync(schedule, clock.GetUtcNow(), ct);
        await audit.WriteAsync("staffreport.run.manual", "StaffReportRun", run.Id.ToString(), new { schedule.Name, run.Delivered }, ct);
        return RunDto(run);
    }

    public async Task<IReadOnlyList<StaffReportRunDto>> RunsAsync(int take, CancellationToken ct = default)
        => (await db.StaffReportRuns.AsNoTracking()
                .OrderByDescending(r => r.GeneratedAt).Take(Math.Clamp(take, 1, 200))
                .Select(r => new { r.Id, r.StaffReportScheduleId, r.Kind, r.Title, r.Summary, r.PeriodStart, r.PeriodEnd, r.GeneratedAt, r.Delivered, r.DeliveryNote })
                .ToListAsync(ct))
            .Select(r => new StaffReportRunDto(r.Id, r.StaffReportScheduleId, r.Kind, r.Title, r.Summary, r.PeriodStart, r.PeriodEnd, r.GeneratedAt, r.Delivered, r.DeliveryNote))
            .ToList();

    public async Task<(string FileName, string ContentType, byte[] Content)> RunFileAsync(Guid runId, string format, CancellationToken ct = default)
    {
        var run = await db.StaffReportRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("Report");
        var baseName = StaffReportGenerator.FileBase(run.Title);
        return format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
            ? ($"{baseName}.pdf", "application/pdf", run.Pdf)
            : ($"{baseName}.csv", "text/csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(run.Csv)).ToArray());
    }

    public async Task<(string FileName, byte[] Content)> ClientQbrPdfAsync(Guid clientCompanyId, int year, int quarter, CancellationToken ct = default)
    {
        if (quarter is < 1 or > 4 || year is < 2000 or > 2100) throw new ValidationFailedException("Choose a quarter from 1 to 4.");
        var start = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        var q = await qbrs.BuildAsync(Org, clientCompanyId, StaffReportFrequency.Quarterly, start, start.AddMonths(3).AddDays(-1), ct);
        return ($"{StaffReportGenerator.FileBase(q.Title)}.pdf", ClientQbrRenderer.ToPdf(q));
    }

    public async Task<(string FileName, byte[] Content)> TechnicianPdfAsync(DateTimeOffset from, DateTimeOffset to, Guid? clientCompanyId, string label, CancellationToken ct = default)
    {
        if (to < from) throw new ValidationFailedException("The end of the range is before its start.");
        if (to - from > TimeSpan.FromDays(400)) throw new ValidationFailedException("Choose a range of at most a year.");
        var zone = await ZoneAsync(ct);
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var report = await builder.BuildAsync(Org, from, to, start, end, string.IsNullOrWhiteSpace(label) ? "Custom range" : label.Trim(), clientCompanyId, ct);
        return ($"{StaffReportGenerator.FileBase(report.Title)}.pdf", TechnicianReportRenderer.ToPdf(report));
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
        => StaffReportPeriods.Zone(await db.MspOrganizations.AsNoTracking().Where(o => o.Id == Org).Select(o => o.TimeZone).FirstOrDefaultAsync(ct));

    private async Task<Dictionary<Guid, string>> ClientNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0 ? [] : await db.ClientCompanies.AsNoTracking().Where(c => list.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    private static StaffReportScheduleDto Dto(StaffReportSchedule s, Dictionary<Guid, string> names) => new(
        s.Id, s.Name, s.Kind, s.Frequency, s.ClientCompanyId,
        s.ClientCompanyId is { } c ? names.GetValueOrDefault(c) : null, s.Recipients, s.IsEnabled, s.LastRunAt, s.NextRunAt);

    internal static StaffReportRunDto RunDto(StaffReportRun r) => new(
        r.Id, r.StaffReportScheduleId, r.Kind, r.Title, r.Summary, r.PeriodStart, r.PeriodEnd, r.GeneratedAt, r.Delivered, r.DeliveryNote);
}

/// <summary>Turns one schedule into one stored, delivered run. Scoped to whichever tenant the caller established.</summary>
public sealed class StaffReportGenerator(
    DeskDbContext db,
    IStaffReportContent content,
    IEmailSender email,
    ILogger<StaffReportGenerator> logger)
{
    public async Task<StaffReportRun> GenerateAndDeliverAsync(StaffReportSchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        var zoneId = await db.MspOrganizations.AsNoTracking()
            .Where(o => o.Id == schedule.MspOrganizationId).Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        var zone = StaffReportPeriods.Zone(zoneId);
        var (start, end) = StaffReportPeriods.LastComplete(schedule.Frequency, now, zone);
        var (from, to) = StaffReportPeriods.UtcBounds(start, end, zone);
        var label = StaffReportPeriods.Describe(schedule.Frequency, start, end);

        var rendered = await content.RenderAsync(schedule, from, to, start, end, label, ct);

        var run = new StaffReportRun
        {
            MspOrganizationId = schedule.MspOrganizationId,
            StaffReportScheduleId = schedule.Id,
            Kind = schedule.Kind,
            ClientCompanyId = schedule.ClientCompanyId,
            PeriodStart = start,
            PeriodEnd = end,
            GeneratedAt = now,
            Title = rendered.Title,
            Summary = rendered.Summary,
            Csv = rendered.Csv,
            Pdf = rendered.Pdf,
        };

        (run.Delivered, run.DeliveryNote) = await DeliverAsync(schedule, rendered, ct);
        db.StaffReportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<(bool, string)> DeliverAsync(StaffReportSchedule schedule, RenderedStaffReport rendered, CancellationToken ct)
    {
        var (valid, _) = EmailAddresses.Parse(schedule.Recipients);
        if (valid.Count == 0) return (false, "No recipients set — report available in the portal.");
        if (!(await email.StatusAsync(schedule.MspOrganizationId, ct)).Configured) return (false, "Email delivery is not configured; report available for download in the portal.");

        try
        {
            var fileBase = FileBase(rendered.Title);
            await email.SendAsync(schedule.MspOrganizationId, new EmailMessage(valid, rendered.Title, rendered.EmailText,
                Attachments:
                [
                    new EmailAttachment($"{fileBase}.pdf", "application/pdf", rendered.Pdf),
                    new EmailAttachment($"{fileBase}.csv", "text/csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(rendered.Csv)).ToArray()),
                ]), ct);
            return (true, $"Emailed to {valid.Count} recipient{(valid.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Staff report '{Title}' could not be emailed", rendered.Title);
            var message = $"Email could not be sent ({ex.GetType().Name}: {ex.Message}); report available in the portal.";
            return (false, message.Length > 500 ? message[..497] + "..." : message);
        }
    }

    /// <summary>A file name anyone's mail client and file system accept.</summary>
    public static string FileBase(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return s.Length > 80 ? s[..80].TrimEnd('-') : s;
    }
}

public sealed record RenderedStaffReport(string Title, string Summary, string Csv, byte[] Pdf, string EmailText);

/// <summary>Produces a schedule's report content. One implementation per kind, chosen by the schedule.</summary>
public interface IStaffReportContent
{
    Task<RenderedStaffReport> RenderAsync(StaffReportSchedule schedule, DateTimeOffset from, DateTimeOffset to,
        DateOnly start, DateOnly end, string label, CancellationToken ct);
}

public sealed class StaffReportContent(TechnicianReportBuilder technicianReports, ClientQbrBuilder qbrs) : IStaffReportContent
{
    public async Task<RenderedStaffReport> RenderAsync(StaffReportSchedule schedule, DateTimeOffset from, DateTimeOffset to,
        DateOnly start, DateOnly end, string label, CancellationToken ct)
    {
        switch (schedule.Kind)
        {
            case StaffReportKind.TechnicianProductivity:
            {
                var r = await technicianReports.BuildAsync(schedule.MspOrganizationId, from, to, start, end, label, schedule.ClientCompanyId, ct);
                var text = new StringBuilder()
                    .AppendLine(r.Title).AppendLine()
                    .AppendLine($"{r.TotalHours:0.##} hours logged ({r.BillablePct}% billable), {r.TotalResolved} tickets resolved, {r.Technicians.Count} people with activity.")
                    .AppendLine();
                foreach (var t in r.Technicians.Take(15))
                    text.AppendLine($"  {t.Name}: {t.Hours:0.##}h, {t.Resolved} resolved");
                if (r.Technicians.Count > 15) text.AppendLine($"  … and {r.Technicians.Count - 15} more in the attached report.");
                text.AppendLine().AppendLine($"The full report is attached as PDF and CSV. Schedule: {schedule.Name}.");
                return new RenderedStaffReport(r.Title, r.Summary, TechnicianReportRenderer.ToCsv(r), TechnicianReportRenderer.ToPdf(r), text.ToString());
            }
            case StaffReportKind.ClientQbr:
            {
                var clientId = schedule.ClientCompanyId ?? throw new ValidationFailedException("A business review needs a client.");
                var q = await qbrs.BuildAsync(schedule.MspOrganizationId, clientId, schedule.Frequency, start, end, ct);
                var text = new StringBuilder()
                    .AppendLine(q.Title).AppendLine()
                    .AppendLine($"{q.Current.Raised} tickets raised ({q.Previous.Raised} in {q.PreviousLabel}), {q.Current.Resolved} resolved, {q.Current.OpenAtEnd} open at the end.")
                    .AppendLine($"{q.Current.Hours:0.##} hours worked, {q.Current.BillableHours:0.##} billable.")
                    .AppendLine(q.Current.SlaPct is { } sla ? $"{sla:0.#}% resolved within SLA ({q.Current.WithinSla} of {q.Current.SlaEligible})." : "No resolved ticket carried an SLA target.")
                    .AppendLine().AppendLine($"The full review is attached as PDF and CSV. Schedule: {schedule.Name}.");
                return new RenderedStaffReport(q.Title, q.Summary, ClientQbrRenderer.ToCsv(q), ClientQbrRenderer.ToPdf(q), text.ToString());
            }
            default:
                throw new ValidationFailedException("This report type is not available yet.");
        }
    }
}

/// <summary>
/// Finds due schedules across every organization, then generates each inside its OWN tenant scope —
/// the metrics queries are tenant-filtered, and under platform scope a report would count every
/// organization's work as one desk.
/// </summary>
public sealed class StaffReportRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<StaffReportRunner> logger) : IStaffReportRunner
{
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        List<(Guid Id, Guid Org)> due;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            due = (await db.StaffReportSchedules.AsNoTracking()
                    .Where(s => s.IsEnabled && s.NextRunAt <= now)
                    .OrderBy(s => s.NextRunAt).Take(100)
                    .Select(s => new { s.Id, s.MspOrganizationId })
                    .ToListAsync(ct))
                .Select(s => (s.Id, s.MspOrganizationId)).ToList();
        }

        var processed = 0;
        foreach (var (id, org) in due)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ISettableTenantContext>().SetTenant(org);
            var db = scope.ServiceProvider.GetRequiredService<DeskDbContext>();
            var generator = scope.ServiceProvider.GetRequiredService<StaffReportGenerator>();
            var schedule = await db.StaffReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (schedule is null) continue;

            try
            {
                await generator.GenerateAndDeliverAsync(schedule, now, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Staff report schedule {ScheduleId} failed to generate", id);
            }

            // Advanced even after a failure: retrying every five minutes would flood the log and, once
            // it recovered, email a stale period. The next period runs on time; Run now covers this one.
            var zone = StaffReportPeriods.Zone(await db.MspOrganizations.AsNoTracking()
                .Where(o => o.Id == org).Select(o => o.TimeZone).FirstOrDefaultAsync(ct));
            db.ChangeTracker.Clear();
            var fresh = await db.StaffReportSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (fresh is null) continue;
            fresh.LastRunAt = now;
            fresh.NextRunAt = StaffReportPeriods.NextRun(fresh.Frequency, now, zone);
            await db.SaveChangesAsync(ct);
        }

        if (processed > 0) logger.LogInformation("Generated {Count} staff report(s)", processed);
        return processed;
    }
}
