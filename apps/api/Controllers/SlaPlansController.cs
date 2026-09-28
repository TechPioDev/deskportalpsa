using Desk.Api.Auth;
using Desk.Application.Boards;
using Desk.Application.Common;
using Desk.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Desk.Api.Controllers;

/// <summary>
/// SLA plans for the team's own boards. Reading them is open to anybody who raises tickets — the
/// raise form shows which plan a topic runs on — while defining them is a lead's decision.
/// Behind the same switch as the boards themselves: a plan applies to nothing else.
/// </summary>
[ApiController]
[Route("api/boards/sla-plans")]
[Authorize]
public sealed class SlaPlansController(ISlaPlanService plans, BoardFeatureOptions features) : ControllerBase, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!features.InternalBoards) throw new NotFoundException("Internal boards");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    [HttpGet]
    [RequirePermission(Permissions.TicketsCreate)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await plans.ListAsync(includeInactive, ct));

    [HttpPost]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Create([FromBody] SlaPlanInput input, CancellationToken ct)
        => Ok(await plans.SaveAsync(null, input, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SlaPlanInput input, CancellationToken ct)
        => Ok(await plans.SaveAsync(id, input, ct));

    [HttpPut("{id:guid}/active")]
    [RequirePermission(Permissions.BoardsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] BoardsController.SetActiveRequest req, CancellationToken ct)
    {
        await plans.SetActiveAsync(id, req.Active, ct);
        return NoContent();
    }
}
