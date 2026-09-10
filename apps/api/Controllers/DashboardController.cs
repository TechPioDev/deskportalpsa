using System.Globalization;
using System.Text;
using Desk.Application.Abstractions;
using Desk.Application.Analytics;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Desk.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// Staff productivity dashboards (technician + manager). Team-wide views require the team
/// productivity permission; export mirrors the team query. Every response carries the operational-
/// indicator disclaimer so it travels with the numbers.
/// </summary>
// Class-level [Authorize] as a floor: every action here also carries [RequirePermission],
// but that is opt-in per action — an action added later without one would otherwise be
// reachable anonymously. This makes authentication the default and the omission harmless.
[Authorize]
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(
    ITechnicianMetricsService metrics, IClientWorkloadService clients,
    IPortalCoverageService coverage, ICurrentUser user) : ControllerBase
{
    [HttpGet("technician")]
    [RequirePermission(Permissions.ProductivityViewOwn)]
    public async Task<IActionResult> Technician([FromQuery] DashboardQuery q, CancellationToken ct)
    {
        var filter = q.ToFilter();

        // The technician filter arrives from the query string, so on its own this endpoint let
        // anyone holding the own-productivity permission read a NAMED colleague's numbers by
        // passing their id — or the whole organization's by passing nothing at all, since an
        // absent filter means "no restriction" downstream. Callers without the team permission are
        // therefore pinned to themselves regardless of what they asked for.
        if (!user.HasPermission(Permissions.ProductivityViewTeam))
        {
            // Pinned to whichever identity this person actually has. A technician who exists only
            // in the portal has no PSA id, and refusing them their own figures - which is what
            // happened here - told the larger half of a desk that their work does not count.
            var self = user.TechnicianExternalId;
            filter = !string.IsNullOrEmpty(self)
                ? filter with { TechnicianExternalId = self, AppUserId = null }
                : user.UserId is { } uid
                    ? filter with { AppUserId = uid, TechnicianExternalId = null }
                    : throw new ForbiddenException(
                        "Your sign-in is not linked to a portal user, so it has no productivity figures to show.");
        }

        var m = await metrics.ForTechnicianAsync(filter, q.ToWeights(), ct);
        return Ok(new { metrics = m, disclaimer = ProductivityScore.Disclaimer });
    }

    /// <summary>
    /// Per technician, per day: hours logged and tickets resolved over the requested range.
    ///
    /// One endpoint rather than one per period. A week, a month, a quarter and an arbitrary range
    /// differ only in their bounds, and four endpoints would be four places to fix the next time
    /// attribution changes.
    ///
    /// Someone without the team permission gets their own series, pinned the same way the technician
    /// endpoint pins - passing a colleague's id must not read their figures.
    /// </summary>
    [HttpGet("daily")]
    // Both audiences: a technician reading their own days, and a manager reading the team's. The
    // own permission belongs to the Technician role and the team one to managers and admins, so
    // gating on either alone locks out exactly the people the view is for.
    [RequirePermission(Permissions.ProductivityViewOwn, Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> Daily([FromQuery] DashboardQuery q, CancellationToken ct)
    {
        var filter = q.ToFilter();
        if (!user.HasPermission(Permissions.ProductivityViewTeam))
        {
            var self = user.TechnicianExternalId;
            filter = !string.IsNullOrEmpty(self)
                ? filter with { TechnicianExternalId = self, AppUserId = null }
                : user.UserId is { } uid
                    ? filter with { AppUserId = uid, TechnicianExternalId = null }
                    : throw new ForbiddenException(
                        "Your sign-in is not linked to a portal user, so it has no productivity figures to show.");
        }

        return Ok(await metrics.DailyAsync(filter, ct));
    }

    /// <summary>
    /// Where the desk's capacity goes, by client. Gated on the TEAM permission: this is
    /// organization-wide commercial information, not someone's own figures.
    /// </summary>
    [HttpGet("clients")]
    [RequirePermission(Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> Clients([FromQuery] DashboardQuery q, CancellationToken ct)
        => Ok(await clients.ForClientsAsync(q.ToFilter(), ct));

    /// <summary>
    /// How much of the work the PSA recorded is visible in this portal. Team-gated: it names
    /// individuals, and it is an operational measure of rollout rather than of people.
    /// </summary>
    [HttpGet("coverage")]
    [RequirePermission(Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> Coverage([FromQuery] DashboardQuery q, CancellationToken ct)
        => Ok(await coverage.CoverageAsync(q.ToFilter(), ct));

    [HttpGet("team")]
    [RequirePermission(Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> Team([FromQuery] DashboardQuery q, CancellationToken ct)
    {
        var rows = await metrics.TeamAsync(q.ToFilter(), q.ToWeights(), ct);
        return Ok(new { team = rows, disclaimer = ProductivityScore.Disclaimer });
    }

    [HttpGet("trend")]
    [RequirePermission(Permissions.ReportsView)]
    public async Task<IActionResult> Trend([FromQuery] DashboardQuery q, CancellationToken ct)
        => Ok(await metrics.TrendAsync(q.ToFilter(), ct));

    [HttpGet("team/export")]
    [RequirePermission(Permissions.ProductivityViewTeam)]
    public async Task<IActionResult> ExportTeam([FromQuery] DashboardQuery q, CancellationToken ct)
    {
        var rows = await metrics.TeamAsync(q.ToFilter(), q.ToWeights(), ct);
        var sb = new StringBuilder();
        sb.AppendLine("# " + ProductivityScore.Disclaimer);
        sb.AppendLine("TechnicianExternalId,Resolved,SlaCompliancePct,ProductivityScore");
        foreach (var r in rows)
            sb.AppendLine(string.Join(',',
                Csv(r.TechnicianExternalId),
                r.Resolved.ToString(CultureInfo.InvariantCulture),
                r.SlaCompliancePct.ToString(CultureInfo.InvariantCulture),
                (r.Score?.ToString(CultureInfo.InvariantCulture) ?? "")));

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "team-productivity.csv");
    }

    // Escapes a CSV field so a technician id containing a comma or quote can't break the columns.
    private static string Csv(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    public sealed record DashboardQuery
    {
        public DateTimeOffset? From { get; init; }
        public DateTimeOffset? To { get; init; }
        public string? Technician { get; init; }
        public Guid? CompanyId { get; init; }
        public Guid? ConnectionId { get; init; }
        public string? Priority { get; init; }

        // Optional weight overrides (configurable score model).
        public double? WSla { get; init; }
        public double? WResolution { get; init; }
        public double? WCsat { get; init; }
        public double? WFirstResponse { get; init; }
        public double? WReopen { get; init; }
        public double? WWorklog { get; init; }
        public double? WDocumentation { get; init; }

        /// <summary>A PORTAL technician to narrow to, where Technician narrows to a PSA one.</summary>
        public Guid? AppUserId { get; init; }

        public MetricsFilter ToFilter() => new()
        {
            From = From, To = To, TechnicianExternalId = Technician, AppUserId = AppUserId,
            ClientCompanyId = CompanyId, PsaConnectionId = ConnectionId, Priority = Priority,
        };

        public ProductivityWeights ToWeights()
        {
            var d = ProductivityWeights.Default;
            return new ProductivityWeights
            {
                SlaCompliance = WSla ?? d.SlaCompliance,
                ResolutionRate = WResolution ?? d.ResolutionRate,
                CustomerSatisfaction = WCsat ?? d.CustomerSatisfaction,
                FirstResponse = WFirstResponse ?? d.FirstResponse,
                ReopenScore = WReopen ?? d.ReopenScore,
                WorklogQuality = WWorklog ?? d.WorklogQuality,
                DocumentationQuality = WDocumentation ?? d.DocumentationQuality,
            };
        }
    }
}
