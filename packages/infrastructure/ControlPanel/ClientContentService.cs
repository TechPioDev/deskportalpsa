using Desk.Application.Admin;
using Desk.Application.Common;
using Desk.Application.ControlPanel;
using Desk.Application.Tickets;
using Desk.Domain.ControlPanel;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Infrastructure.ControlPanel;

/// <summary>
/// CP-3 content service. Announcements + branding are per-account and gated by section access;
/// the report is a read-only projection of the account's synced tickets. Timestamps come from the
/// shared clock (via SaveChanges) so results stay deterministic.
/// </summary>
public sealed class ClientContentService(DeskDbContext db, IAuditWriter audit, TimeProvider clock, IReportDelivery delivery) : IClientContentService
{

    // ---- Announcements ----

    public async Task<IReadOnlyList<AnnouncementDto>> ListAnnouncementsAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Announcements, ct);
        return await db.Announcements.AsNoTracking()
            .Where(a => a.ClientCompanyId == access.ClientCompanyId)
            .OrderByDescending(a => a.IsPinned).ThenByDescending(a => a.PublishedAt ?? a.CreatedAt)
            .Select(a => new AnnouncementDto(a.Id, a.Title, a.Body, a.IsPinned, a.IsPublished, a.PublishedAt, a.AuthorName))
            .ToListAsync(ct);
    }

    public async Task<AnnouncementDto> SaveAnnouncementAsync(ClientAccess access, AnnouncementInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Announcements, ct);
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0) throw new ValidationFailedException("Title is required.");

        var author = await db.ClientUsers.AsNoTracking()
            .Where(u => u.Id == access.ClientUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

        Announcement row;
        if (input.Id is { } id)
        {
            row = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id && a.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("Announcement");
        }
        else
        {
            row = new Announcement { ClientCompanyId = access.ClientCompanyId, Title = title };
            db.Announcements.Add(row);
        }

        row.Title = title;
        row.Body = input.Body ?? "";
        row.IsPinned = input.IsPinned;
        // Stamp the publish time when it first becomes published.
        if (input.IsPublished && (!row.IsPublished || row.PublishedAt is null)) row.PublishedAt = clock.GetUtcNow();
        row.IsPublished = input.IsPublished;
        row.AuthorName = author;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.announcement.save", nameof(Announcement), row.Id.ToString(), new { row.IsPublished }, ct);
        return new AnnouncementDto(row.Id, row.Title, row.Body, row.IsPinned, row.IsPublished, row.PublishedAt, row.AuthorName);
    }

    public async Task DeleteAnnouncementAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Announcements, ct);
        var row = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id && a.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("Announcement");
        db.Announcements.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.announcement.delete", nameof(Announcement), id.ToString(), null, ct);
    }

    // ---- Branding ----

    public async Task<BrandingDto> GetBrandingAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Branding, ct);
        var row = await db.ClientBrandings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientCompanyId == access.ClientCompanyId, ct);
        return new BrandingDto(row?.DisplayName, row?.LogoUrl, row?.AccentColor);
    }

    public async Task<BrandingDto> SaveBrandingAsync(ClientAccess access, BrandingInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Branding, ct);
        var row = await db.ClientBrandings.FirstOrDefaultAsync(x => x.ClientCompanyId == access.ClientCompanyId, ct);
        if (row is null)
        {
            row = new ClientBranding { ClientCompanyId = access.ClientCompanyId };
            db.ClientBrandings.Add(row);
        }
        row.DisplayName = Trim(input.DisplayName);
        row.LogoUrl = Trim(input.LogoUrl);
        row.AccentColor = Trim(input.AccentColor);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.branding.save", nameof(ClientBranding), row.Id.ToString(), null, ct);
        return new BrandingDto(row.DisplayName, row.LogoUrl, row.AccentColor);
    }

    // ---- Report ----

    public async Task<AccountReportDto> GetReportAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        return await ReportComposer.BuildAsync(db, access.ClientCompanyId, ct);
    }

    public async Task<ReportExportDto> ExportReportAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        var report = await ReportComposer.BuildAsync(db, access.ClientCompanyId, ct);
        var company = await CompanyNameAsync(access.ClientCompanyId, ct);
        var now = clock.GetUtcNow();
        var csv = ReportComposer.ToCsv(report, company, now);
        return new ReportExportDto(FileName(company, now), csv);
    }

    // ---- Report schedules ----

    public async Task<IReadOnlyList<ReportScheduleDto>> ListSchedulesAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        return await db.ReportSchedules.AsNoTracking()
            .Where(s => s.ClientCompanyId == access.ClientCompanyId)
            .OrderBy(s => s.Name)
            .Select(s => new ReportScheduleDto(s.Id, s.Name, s.Frequency.ToString().ToLowerInvariant(), s.Recipients, s.IsEnabled, s.LastRunAt, s.NextRunAt))
            .ToListAsync(ct);
    }

    public async Task<ReportScheduleDto> SaveScheduleAsync(ClientAccess access, ReportScheduleInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new ValidationFailedException("Name is required.");
        var freq = ParseFrequency(input.Frequency);

        ReportSchedule row;
        if (input.Id is { } id)
        {
            row = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == id && s.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("Report schedule");
        }
        else
        {
            row = new ReportSchedule { ClientCompanyId = access.ClientCompanyId, Name = name };
            db.ReportSchedules.Add(row);
        }

        // Recompute the next run when the schedule is new or its frequency changed.
        var freqChanged = row.Frequency != freq;
        row.Name = name;
        row.Frequency = freq;
        row.Recipients = Trim(input.Recipients);
        row.IsEnabled = input.IsEnabled;
        if (input.Id is null || freqChanged || row.NextRunAt == default)
            row.NextRunAt = ReportComposer.Advance(clock.GetUtcNow(), freq);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.report_schedule.save", nameof(ReportSchedule), row.Id.ToString(), new { row.Frequency, row.IsEnabled }, ct);
        return new ReportScheduleDto(row.Id, row.Name, row.Frequency.ToString().ToLowerInvariant(), row.Recipients, row.IsEnabled, row.LastRunAt, row.NextRunAt);
    }

    public async Task DeleteScheduleAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        var row = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == id && s.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("Report schedule");
        db.ReportSchedules.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.report_schedule.delete", nameof(ReportSchedule), id.ToString(), null, ct);
    }

    public async Task<ReportRunDto> RunScheduleNowAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        var schedule = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == id && s.ClientCompanyId == access.ClientCompanyId, ct)
                       ?? throw new NotFoundException("Report schedule");
        var run = await GenerateRunAsync(access.ClientCompanyId, schedule, ct);
        await audit.WriteAsync("control_panel.report_schedule.run", nameof(ReportRun), run.Id.ToString(), new { schedule = schedule.Id }, ct);
        return ToRunDto(run);
    }

    public async Task<IReadOnlyList<ReportRunDto>> ListRunsAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        return await db.ReportRuns.AsNoTracking()
            .Where(r => r.ClientCompanyId == access.ClientCompanyId)
            .OrderByDescending(r => r.GeneratedAt).Take(50)
            .Select(r => new ReportRunDto(r.Id, r.ReportScheduleId, r.GeneratedAt, r.Format, r.Summary, r.Delivered, r.DeliveryNote))
            .ToListAsync(ct);
    }

    public async Task<ReportExportDto> DownloadRunAsync(ClientAccess access, Guid runId, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.Reports, ct);
        var run = await db.ReportRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.ClientCompanyId == access.ClientCompanyId, ct)
            ?? throw new NotFoundException("Report run");
        var company = await CompanyNameAsync(access.ClientCompanyId, ct);
        return new ReportExportDto(FileName(company, run.GeneratedAt), run.Content);
    }

    /// <summary>Build, store and deliver a report run for a schedule; advances the schedule's next run.</summary>
    private async Task<ReportRun> GenerateRunAsync(Guid companyId, ReportSchedule schedule, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var report = await ReportComposer.BuildAsync(db, companyId, ct);
        var company = await CompanyNameAsync(companyId, ct);
        var csv = ReportComposer.ToCsv(report, company, now);

        var run = new ReportRun
        {
            MspOrganizationId = schedule.MspOrganizationId,
            ClientCompanyId = companyId,
            ReportScheduleId = schedule.Id,
            GeneratedAt = now,
            Format = "csv",
            Summary = ReportComposer.Summary(report),
            Content = csv,
        };
        db.ReportRuns.Add(run);

        var result = await delivery.DeliverAsync(schedule.MspOrganizationId, schedule.Recipients, $"{company} — scheduled report", FileName(company, now), csv, ct);
        run.Delivered = result.Delivered;
        run.DeliveryNote = result.Note;

        schedule.LastRunAt = now;
        schedule.NextRunAt = ReportComposer.Advance(now, schedule.Frequency);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<string> CompanyNameAsync(Guid companyId, CancellationToken ct)
        => await db.ClientCompanies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "Account";

    private static string FileName(string company, DateTimeOffset at)
    {
        var slug = new string(company.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        if (slug.Length == 0) slug = "account";
        return $"{slug}-report-{at:yyyyMMdd}.csv";
    }

    private static ReportRunDto ToRunDto(ReportRun r)
        => new(r.Id, r.ReportScheduleId, r.GeneratedAt, r.Format, r.Summary, r.Delivered, r.DeliveryNote);

    private static ReportFrequency ParseFrequency(string? f)
        => Enum.TryParse<ReportFrequency>((f ?? "").Trim(), ignoreCase: true, out var v) ? v : ReportFrequency.Weekly;

    // ---- Knowledge base / FAQ ----

    public async Task<IReadOnlyList<FaqArticleDto>> ListFaqAsync(ClientAccess access, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.KnowledgeBase, ct);
        return await db.FaqArticles.AsNoTracking()
            .Where(f => f.ClientCompanyId == access.ClientCompanyId)
            .OrderBy(f => f.Category).ThenBy(f => f.SortOrder).ThenBy(f => f.Question)
            .Select(f => new FaqArticleDto(f.Id, f.Question, f.Answer, f.Category, f.IsPublished, f.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<FaqArticleDto> SaveFaqAsync(ClientAccess access, FaqArticleInput input, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.KnowledgeBase, ct);
        var question = (input.Question ?? "").Trim();
        if (question.Length == 0) throw new ValidationFailedException("Question is required.");

        FaqArticle row;
        if (input.Id is { } id)
        {
            row = await db.FaqArticles.FirstOrDefaultAsync(f => f.Id == id && f.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("FAQ article");
        }
        else
        {
            row = new FaqArticle { ClientCompanyId = access.ClientCompanyId, Question = question };
            db.FaqArticles.Add(row);
        }

        row.Question = question;
        row.Answer = input.Answer ?? "";
        row.Category = Trim(input.Category);
        row.IsPublished = input.IsPublished;
        row.SortOrder = input.SortOrder;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("control_panel.faq.save", nameof(FaqArticle), row.Id.ToString(), new { row.IsPublished }, ct);
        return new FaqArticleDto(row.Id, row.Question, row.Answer, row.Category, row.IsPublished, row.SortOrder);
    }

    public async Task DeleteFaqAsync(ClientAccess access, Guid id, CancellationToken ct = default)
    {
        await EnsureSectionAsync(access, ControlPanelSection.KnowledgeBase, ct);
        var row = await db.FaqArticles.FirstOrDefaultAsync(f => f.Id == id && f.ClientCompanyId == access.ClientCompanyId, ct)
                  ?? throw new NotFoundException("FAQ article");
        db.FaqArticles.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("control_panel.faq.delete", nameof(FaqArticle), id.ToString(), null, ct);
    }

    // ---- Helpers ----

    private async Task EnsureSectionAsync(ClientAccess access, ControlPanelSection section, CancellationToken ct)
    {
        if (access.IsCompanyAdministrator) return;
        var granted = await db.ClientAccessGrants.AsNoTracking()
            .AnyAsync(g => g.ClientUserId == access.ClientUserId && g.Section == section, ct);
        if (!granted) throw new ForbiddenException("You do not have access to this section.");
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
