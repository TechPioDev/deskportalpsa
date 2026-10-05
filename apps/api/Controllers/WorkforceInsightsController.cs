using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Common;
using Desk.Application.Workforce;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>The query string management insights and reports take; every id is validated by the service against the tenant and the caller's scope.</summary>
public sealed record InsightsQueryInput(
    string? Window = null, DateOnly? From = null, DateOnly? To = null,
    Guid? TeamId = null, Guid? DepartmentId = null, Guid? AppUserId = null,
    Guid? ClientId = null, string? Source = null, string? Priority = null,
    string? Compare = null, string? Period = null, string? By = null)
{
    public InsightsQuery ToQuery(Guid? skillId = null) => new(Window, From, To, TeamId, DepartmentId, AppUserId, ClientId, Source, Priority, Compare, Period, By, skillId);
}

/// <summary>
/// Management insights (Phase 8): the capacity forecast, what changed against the period before, the
/// quality signals the records support, and what needs attention, for the people the caller's
/// <c>schedule.view</c> scope reaches. Reads only; every action is a GET. Staff only, behind the
/// Workforce switch: a client account is refused before any action runs. Deterministic sums over
/// records: nothing is predicted statistically and nothing scores a person.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce/insights")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceInsightsController(IWorkforceInsightsService insights, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    /// <summary>Capacity ahead against confirmed, tentative and estimated unscheduled demand, by day, team, person, client, source and skill, with the attention list.</summary>
    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast([FromQuery] InsightsQueryInput q, CancellationToken ct)
        => Ok(await insights.ForecastAsync(Caller(), q.ToQuery(), ct));

    /// <summary>
    /// The records behind a forecast figure, paged on the server; the totals cover the whole set so the
    /// list reconciles with the figure. <paramref name="list"/> is confirmed, tentative, unscheduled,
    /// unestimated, at-risk, overdue, unassigned or skill (with <paramref name="skillId"/>).
    /// </summary>
    [HttpGet("forecast/work")]
    public async Task<IActionResult> ForecastWork([FromQuery] string list, [FromQuery] InsightsQueryInput q, [FromQuery] Guid? skillId = null,
        [FromQuery] int skip = 0, [FromQuery] int take = WorkforceAnalyticsDefaults.Take, CancellationToken ct = default)
    {
        var parsed = (list ?? "").Trim().ToLowerInvariant() switch
        {
            "confirmed" => ForecastWorkKind.Confirmed,
            "tentative" => ForecastWorkKind.Tentative,
            "unscheduled" => ForecastWorkKind.Unscheduled,
            "unestimated" => ForecastWorkKind.Unestimated,
            "at-risk" or "atrisk" => ForecastWorkKind.AtRisk,
            "overdue" => ForecastWorkKind.Overdue,
            "skill" => ForecastWorkKind.Skill,
            "unassigned" => ForecastWorkKind.Unassigned,
            _ => throw new ValidationFailedException("Unknown list. Use confirmed, tentative, unscheduled, unestimated, at-risk, overdue, unassigned or skill."),
        };
        if (parsed == ForecastWorkKind.Skill && skillId is null) throw new ValidationFailedException("The skill list needs a skillId.");
        return Ok(await insights.ForecastWorkAsync(Caller(), q.ToQuery(skillId), parsed, skip, take, ct));
    }

    /// <summary>A period against the one before it: totals, the last eight weeks, clients, sources, estimate variance and quality signals.</summary>
    [HttpGet("trends")]
    public async Task<IActionResult> Trends([FromQuery] InsightsQueryInput q, CancellationToken ct)
        => Ok(await insights.TrendsAsync(Caller(), q.ToQuery(), ct));

    /// <summary>Per PSA connection: sync state and how well its records map. No credential, address or error text.</summary>
    [HttpGet("health")]
    [RequirePermission(Permissions.IntegrationHealthView)]
    public async Task<IActionResult> Health(CancellationToken ct) => Ok(await insights.HealthAsync(Caller(), ct));
}

/// <summary>
/// The report center (Phase 8): a catalogue of reports defined in code, a preview and an export
/// (CSV or XLSX) built by the same code from the same query. Nothing is stored, so there is no saved
/// report or generated file to address by id. Staff only, behind the Workforce switch; the export,
/// which carries figures out of the system, needs <c>workforce.analytics.export</c> and is audited.
/// </summary>
[Authorize]
[ApiController]
[Route("api/workforce/reports")]
[RequirePermission(Permissions.ScheduleView)]
public sealed class WorkforceReportsController(IWorkforceReportService reports, ICurrentUser user, WorkforceFeatureOptions features) : ControllerBase
{
    private Guid Caller()
    {
        if (!features.Enabled) throw new NotFoundException("Workforce");
        return user.UserId ?? throw new ForbiddenException("Only staff accounts can use the workforce module.");
    }

    /// <summary>The reports the caller may run.</summary>
    [HttpGet]
    public async Task<IActionResult> Catalogue(CancellationToken ct) => Ok(await reports.CatalogueAsync(Caller(), ct));

    /// <summary>A report as previewed: what was asked, a summary, and the first rows of the table with the full count.</summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> Preview(string key, [FromQuery] InsightsQueryInput q, CancellationToken ct)
        => Ok(await reports.ReportAsync(Caller(), key, q.ToQuery(), ct));

    /// <summary>The same report as a file. Figures leave the system here, so it needs its own permission and is audited.</summary>
    [HttpGet("{key}/export")]
    [RequirePermission(Permissions.WorkforceAnalyticsExport)]
    public async Task<IActionResult> Export(string key, [FromQuery] InsightsQueryInput q, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        var file = await reports.ExportReportAsync(Caller(), key, q.ToQuery(), format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
