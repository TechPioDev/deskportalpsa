using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Workforce analytics (Phase 7): how work is distributed, planned, executed and completed over a
/// period, for the people the caller's <c>schedule.view</c> scope reaches (a technician: their own
/// figures; a manager: their people's). Reads only; every action is a GET. Staff only, behind the
/// Workforce switch: a client account is refused before any action runs. The CSV export, which
/// carries named people's figures out of the system, needs <c>workforce.analytics.export</c> and is
/// audited. Nothing here is attendance or a performance score.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceAnalyticsController(IWorkforceAnalyticsService analytics, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    /// <summary>The query string as the dashboard sends it; every id is validated by the service against the tenant and the caller's scope.</summary>
    public sealed record AnalyticsQueryInput(
        string? Period = null, DateOnly? From = null, DateOnly? To = null,
        Guid? TeamId = null, Guid? DepartmentId = null, Guid? AppUserId = null,
        Guid? ClientId = null, string? Source = null, string? Priority = null, string? Kind = null)
    {
        public AnalyticsQuery ToQuery() => new(Period, From, To, TeamId, DepartmentId, AppUserId, ClientId, Source, Priority, (Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "all" => ActualKindFilter.All,
            "planned" => ActualKindFilter.Planned,
            "reactive" => ActualKindFilter.Reactive,
            _ => throw new ValidationFailedException("Unknown kind. Use all, planned or reactive."),
        });
    }

    /// <summary>The filter lists the caller may use: their teams, departments, people, clients, sources and priorities.</summary>
    [HttpGet("analytics/filters")]
    public async Task<IActionResult> Filters(CancellationToken ct) => Ok(await analytics.FiltersAsync(Caller(), ct));

    /// <summary>The dashboard: cards, breakdowns, daily trend, heatmap and capacity against demand, all under one filter context.</summary>
    [HttpGet("analytics/overview")]
    public async Task<IActionResult> Overview([FromQuery] AnalyticsQueryInput q, CancellationToken ct)
        => Ok(await analytics.OverviewAsync(Caller(), q.ToQuery(), ct));

    /// <summary>One person's period: their figures, breakdowns, days and the work items behind them.</summary>
    [HttpGet("analytics/people/{id:guid}")]
    public async Task<IActionResult> Technician(Guid id, [FromQuery] AnalyticsQueryInput q, CancellationToken ct)
        => Ok(await analytics.TechnicianAsync(Caller(), id, q.ToQuery(), ct));

    /// <summary>
    /// The records behind a figure (drill-down), paged on the server; the totals cover the whole set so
    /// the list reconciles with the card. <paramref name="list"/> names the records ("kind" is the
    /// planned / reactive filter the query carries, so the two cannot share a name).
    /// </summary>
    [HttpGet("analytics/work")]
    public async Task<IActionResult> Work([FromQuery] string list, [FromQuery] AnalyticsQueryInput q, [FromQuery] int skip = 0, [FromQuery] int take = WorkforceAnalyticsDefaults.Take, CancellationToken ct = default)
    {
        var parsed = (list ?? "").Trim().ToLowerInvariant() switch
        {
            "actual" => AnalyticsWorkKind.Actual,
            "planned-actual" or "plannedactual" => AnalyticsWorkKind.PlannedActual,
            "reactive" => AnalyticsWorkKind.Reactive,
            "planned" => AnalyticsWorkKind.Planned,
            "tentative" => AnalyticsWorkKind.Tentative,
            "completed" => AnalyticsWorkKind.Completed,
            "open" => AnalyticsWorkKind.Open,
            "unscheduled" => AnalyticsWorkKind.Unscheduled,
            "overdue" => AnalyticsWorkKind.Overdue,
            _ => throw new ValidationFailedException("Unknown list. Use actual, planned-actual, reactive, planned, tentative, completed, open, unscheduled or overdue."),
        };
        return Ok(await analytics.WorkAsync(Caller(), q.ToQuery(), parsed, skip, take, ct));
    }

    /// <summary>A CSV of one table under the same filters. Named people's figures leave the system here, so it needs its own permission and is audited.</summary>
    [HttpGet("analytics/export")]
    [RequirePermission(Permissions.WorkforceAnalyticsExport)]
    public async Task<IActionResult> Export([FromQuery] string report, [FromQuery] AnalyticsQueryInput q, CancellationToken ct)
    {
        var parsed = (report ?? "").Trim().ToLowerInvariant() switch
        {
            "technicians" => AnalyticsExportReport.Technicians,
            "teams" => AnalyticsExportReport.Teams,
            "clients" => AnalyticsExportReport.Clients,
            "sources" => AnalyticsExportReport.Sources,
            "daily" => AnalyticsExportReport.Daily,
            "work" => AnalyticsExportReport.Work,
            _ => throw new ValidationFailedException("Unknown report. Use technicians, teams, clients, sources, daily or work."),
        };
        var file = await analytics.ExportAsync(Caller(), q.ToQuery(), parsed, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}

/// <summary>Default page size for the drill-down (a constant the attribute can take).</summary>
public static class WorkforceAnalyticsDefaults
{
    public const int Take = 50;
}
