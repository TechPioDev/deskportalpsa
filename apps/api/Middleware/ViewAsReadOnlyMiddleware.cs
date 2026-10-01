using Desk.Api.Auth;
using Desk.Application.Common;

namespace Desk.Api.Middleware;

/// <summary>
/// Viewing as someone is for looking, never for acting: a reply, a status change or a time entry made
/// "as" a colleague would be credited to them and read as theirs. So while viewing as someone, every
/// request that could change anything is refused here, before any controller runs - one rule, with
/// no endpoint able to forget it. Reads pass.
/// </summary>
public sealed class ViewAsReadOnlyMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (ViewAs.IsViewing(context.User)
            && !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)))
        {
            var name = context.User.FindFirst(ViewAs.NameClaim)?.Value ?? "someone else";
            throw new ForbiddenException($"You are viewing as {name}, which is read-only. Exit the view to make changes.");
        }
        return next(context);
    }
}
