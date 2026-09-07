using Desk.Api.Auth;
using Desk.Application.Marketing;
using Desk.Domain.Authorization;
using Desk.Domain.Marketing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Desk.Api.Controllers;

/// <summary>
/// No validation attributes: on a positional record they land on the generated property, and MVC
/// throws rather than binding — a 500 on the one endpoint anonymous visitors touch. Required-ness
/// is enforced in the service, which owns the rules and returns a message worth reading.
/// </summary>
public sealed record PublicEnquiryRequest(
    string Name,
    string Email,
    string? Company,
    string? Phone,
    string Message,
    string? PreferredTime,
    string? SourcePage,
    string? Website);

/// <summary>
/// The public site's only way in. Anonymous by necessity, so it is deliberately narrow: it writes
/// an enquiry and returns nothing but success, never reading or echoing anything.
/// </summary>
[ApiController]
[Route("api/public/enquiries")]
[AllowAnonymous]
[EnableRateLimiting("public-forms")]
public sealed class PublicEnquiriesController(IEnquiryService enquiries) : ControllerBase
{
    [HttpPost("contact")]
    public Task<IActionResult> Contact([FromBody] PublicEnquiryRequest body, CancellationToken ct) =>
        SubmitAsync(EnquiryKind.Contact, body, ct);

    [HttpPost("meeting")]
    public Task<IActionResult> Meeting([FromBody] PublicEnquiryRequest body, CancellationToken ct) =>
        SubmitAsync(EnquiryKind.Meeting, body, ct);

    private async Task<IActionResult> SubmitAsync(EnquiryKind kind, PublicEnquiryRequest body, CancellationToken ct)
    {
        var result = await enquiries.SubmitAsync(new SubmitEnquiryInput(
            kind, body.Name, body.Email, body.Company, body.Phone,
            body.Message, body.PreferredTime, body.SourcePage, body.Website), ct);

        if (result.Accepted) return Accepted(new { received = true });

        // Enough to correct the form, and no more — an anonymous caller should not be able to probe
        // what is stored. The list is per-kind because a meeting needs details a question does not,
        // and naming the wrong fields sends someone to check something that was never the problem.
        var required = kind == EnquiryKind.Meeting
            ? "your name, email address, company, phone number, preferred time and message"
            : "your name, email address and message";

        // An over-long field names itself and its limit. The alternative was what this endpoint did
        // before: accept it, store as much as fits, and let the visitor believe all of it arrived.
        // A number they can act on is the whole point — "too long" without one is a puzzle.
        var error = result.Refusal == EnquiryRefusal.TooLong
            ? $"Your {result.Field} is too long — please shorten it to {result.Limit:N0} characters "
              + "or fewer and send it again. Nothing has been saved yet."
            : $"Please check {required}.";

        return BadRequest(new { error });
    }
}

[ApiController]
[Route("api/admin/enquiries")]
[Authorize]
public sealed class AdminEnquiriesController(IEnquiryService enquiries) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.EnquiriesView)]
    public async Task<IActionResult> List([FromQuery] EnquiryStatus? status, CancellationToken ct) =>
        Ok(await enquiries.ListAsync(status, ct));

    [HttpPost("{id:guid}/status")]
    [RequirePermission(Permissions.EnquiriesView)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetEnquiryStatusRequest body, CancellationToken ct) =>
        await enquiries.SetStatusAsync(id, body.Status, ct) ? NoContent() : NotFound();
}

public sealed record SetEnquiryStatusRequest(EnquiryStatus Status);
