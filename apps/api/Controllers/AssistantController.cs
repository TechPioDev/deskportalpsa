using System.ComponentModel.DataAnnotations;
using Desk.Api.Auth;
using Desk.Application.Abstractions;
using Desk.Application.Admin;
using Desk.Application.Assistant;
using Desk.Application.Common;
using Desk.Application.Tickets;
using Desk.Domain.Assistant;
using Desk.Domain.Authorization;
using Desk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Controllers;

/// <summary>
/// The ticket assistant. Every action is STAFF-only and read-only: it answers questions about a
/// ticket the caller can already see, and writes nothing to the ticket, the thread or the PSA.
/// Clients never reach this controller — the permission it demands is the staff view-all one.
/// </summary>
[ApiController]
[Route("api/assistant")]
[Authorize]
public sealed class AssistantController(
    IAssistantService assistant,
    DeskDbContext db,
    ISecretStore secrets,
    IAuditWriter audit,
    ITicketScopeQuery scopeQuery,
    ICurrentUser user) : ControllerBase
{
    /// <summary>Whether the rail should offer itself, and what to say when it cannot.</summary>
    [HttpGet("availability")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Availability(CancellationToken ct)
        => Ok(await assistant.AvailabilityAsync(ct));

    [HttpPost("tickets/{id:guid}")]
    [RequirePermission(Permissions.TicketsViewAll, Permissions.TicketsViewAssigned)]
    public async Task<IActionResult> Ask(Guid id, [FromBody] AskRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<AssistantAction>(req.Action, ignoreCase: true, out var action))
            throw new ValidationFailedException("Unknown assistant action.");

        // Scope check FIRST: the assistant must never become a way to read a ticket the caller
        // could not open themselves.
        if (user.UserId is not { } uid) throw new NotFoundException("Ticket");
        _ = await scopeQuery.FindAsync(db.Tickets, id, uid, Permissions.TicketsViewAll, ct)
            ?? throw new NotFoundException("Ticket");

        var answer = await assistant.AskAsync(id, action, req.Draft, req.Question, ct);
        return Ok(answer);
    }

    // ---- settings (administrators) ----

    [HttpGet("settings")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var s = await db.AssistantSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        // Never the key itself — only whether one is held, which is all the form needs to render.
        return Ok(new
        {
            isEnabled = s?.IsEnabled ?? false,
            model = s?.Model ?? "gemini-2.0-flash",
            includeInternalNotes = s?.IncludeInternalNotes ?? false,
            hasKey = !string.IsNullOrEmpty(s?.CredentialSecretRef),
        });
    }

    [HttpPut("settings")]
    [RequirePermission(Permissions.ConnectionsManage)]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsRequest req, CancellationToken ct)
    {
        var s = await db.AssistantSettings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new AssistantSettings { MspOrganizationId = user.OrganizationId ?? Guid.Empty };
            db.AssistantSettings.Add(s);
        }

        s.IsEnabled = req.IsEnabled;
        s.IncludeInternalNotes = req.IncludeInternalNotes;
        if (!string.IsNullOrWhiteSpace(req.Model)) s.Model = req.Model.Trim();

        // An empty key means "leave the stored one alone" — the form never receives the existing
        // value, so treating blank as "clear it" would wipe the key on every unrelated save.
        if (!string.IsNullOrWhiteSpace(req.ApiKey))
            s.CredentialSecretRef = await secrets.WriteAsync(
                $"assistant/{s.MspOrganizationId}", new Dictionary<string, string> { ["ApiKey"] = req.ApiKey.Trim() }, ct);

        if (req.IsEnabled && string.IsNullOrEmpty(s.CredentialSecretRef))
            throw new ValidationFailedException("Add a Google API key before switching the assistant on.");

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("assistant.settings.updated", nameof(AssistantSettings), s.Id.ToString(),
            new { s.IsEnabled, s.Model, s.IncludeInternalNotes, keyChanged = !string.IsNullOrWhiteSpace(req.ApiKey) }, ct);

        return Ok(new { isEnabled = s.IsEnabled, model = s.Model, includeInternalNotes = s.IncludeInternalNotes, hasKey = true });
    }

    public sealed record AskRequest(
        [Required] string Action,
        [StringLength(6000)] string? Draft,
        // Capped well below the draft: this is one question about one ticket, not a channel for
        // pushing arbitrary text through the model on the tenant's key.
        [StringLength(2000)] string? Question = null);

    public sealed record SettingsRequest(
        bool IsEnabled,
        bool IncludeInternalNotes,
        [StringLength(100)] string? Model,
        [StringLength(200)] string? ApiKey);
}
