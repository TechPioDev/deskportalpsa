using Desk.Api.Auth;
using Desk.Application.Reporting;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Desk.Api.Controllers;

/// <summary>
/// The MSP's scheduled reports. Gated on TEAM productivity throughout: every report here carries
/// named people's figures, and scheduling one sends those figures to email addresses, so reading
/// and scheduling need the same permission a manager needs to see the team table.
/// </summary>
[Authorize]
[ApiController]
[Route("api/reports")]
[RequirePermission(Permissions.ProductivityViewTeam)]
public sealed class StaffReportsController(IStaffReportService reports) : ControllerBase
{
    public sealed record TimeZoneInput(string TimeZone);

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct) => Ok(new { timeZone = await reports.TimeZoneAsync(ct) });

    [HttpPut("settings")]
    [RequirePermission(Permissions.OrgManage)]
    public async Task<IActionResult> SaveSettings([FromBody] TimeZoneInput input, CancellationToken ct)
        => Ok(new { timeZone = await reports.SetTimeZoneAsync(input.TimeZone, ct) });

    [HttpGet("clients")]
    public async Task<IActionResult> Clients(CancellationToken ct)
        => Ok((await reports.ClientsAsync(ct)).Select(c => new { id = c.Id, name = c.Name }));

    [HttpGet("schedules")]
    public async Task<IActionResult> Schedules(CancellationToken ct) => Ok(await reports.SchedulesAsync(ct));

    [HttpPost("schedules")]
    public async Task<IActionResult> Save([FromBody] StaffReportScheduleInput input, CancellationToken ct)
        => Ok(await reports.SaveScheduleAsync(input, ct));

    [HttpDelete("schedules/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await reports.DeleteScheduleAsync(id, ct);
        return NoContent();
    }

    [HttpPost("schedules/{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct) => Ok(await reports.RunNowAsync(id, ct));

    [HttpGet("runs")]
    public async Task<IActionResult> Runs([FromQuery] int take = 50, CancellationToken ct = default) => Ok(await reports.RunsAsync(take, ct));

    [HttpGet("runs/{id:guid}/{format:regex(^(pdf|csv)$)}")]
    public async Task<IActionResult> Download(Guid id, string format, CancellationToken ct)
    {
        var (name, type, content) = await reports.RunFileAsync(id, format, ct);
        return File(content, type, name);
    }

    [HttpGet("qbr.pdf")]
    public async Task<IActionResult> ClientQbrPdf([FromQuery] Guid companyId, [FromQuery] int year, [FromQuery] int quarter, CancellationToken ct)
    {
        var (name, content) = await reports.ClientQbrPdfAsync(companyId, year, quarter, ct);
        return File(content, "application/pdf", name);
    }

    [HttpGet("technician-productivity.pdf")]
    public async Task<IActionResult> TechnicianPdf(
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, [FromQuery] Guid? companyId, [FromQuery] string? label, CancellationToken ct)
    {
        var (name, content) = await reports.TechnicianPdfAsync(from, to, companyId, label ?? "", ct);
        return File(content, "application/pdf", name);
    }
}
